using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed partial class ShineLabTaskRepository
{
    private async Task<List<ShineLabTaskSample>> AllocateSamplesAsync(
        ShineLabTaskCreateRequest request, CancellationToken cancellationToken)
    {
        var allocated = new List<ShineLabTaskSample>();
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in request.SampleData)
        {
            string id;
            var attempts = 0;
            do
            {
                if (++attempts > 100)
                    throw new InvalidOperationException("Unable to allocate a unique ShineLab sampleId; no Config was sent.");
                // Decimal text fitting signed Int32 avoids UUID/large-integer
                // assumptions in the legacy client. Vendor round-trip still
                // requires field verification. The DB unique index is final.
                id = RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000)
                    .ToString(CultureInfo.InvariantCulture);
            } while (reserved.Contains(id) || await database.ShineLabTaskSamples.AnyAsync(
                item => item.SampleId == id, cancellationToken));
            reserved.Add(id);
            allocated.Add(new ShineLabTaskSample
            {
                TaskUuid = request.TaskUuid.Trim(), EquipmentCode = request.EquipmentCode.Trim().ToUpperInvariant(),
                RowNumber = allocated.Count + 1, SourceSampleId = row.SampleId,
                SampleId = id, SampleName = row.SampleName, Channel = row.Channel
            });
        }
        return allocated;
    }

    public Task<List<ShineLabTaskSample>> GetSamplesAsync(string taskUuid, CancellationToken cancellationToken) =>
        database.ShineLabTaskSamples.Where(item => item.TaskUuid == taskUuid)
            .OrderBy(item => item.RowNumber).ToListAsync(cancellationToken);

    public static string GetResultState(IReadOnlyCollection<ShineLabTaskSample> samples) =>
        samples.Count == 0 ? "NotTracked" : samples.Any(item => item.ResultConflict) ? "NeedsReview" :
        samples.All(item => item.ResultJson is not null) ? "AllResultsReceived" :
        samples.Any(item => item.ResultJson is not null) ? "PartialResultsReceived" : "AwaitingResults";

    private async Task<(ShineLabTaskRecord? Task, ShineLabTaskSample? Sample, string Correlation)> ResolveSampleAsync(
        string equipmentCode, JsonElement body, string? explicitTaskId, ShineLabTaskRecord? explicitTask,
        string originalCorrelation, CancellationToken cancellationToken)
    {
        var id = ReadString(body, "sampleId");
        if (string.IsNullOrWhiteSpace(id)) return (explicitTask, null, originalCorrelation);
        var sample = await database.ShineLabTaskSamples.SingleOrDefaultAsync(item => item.SampleId == id, cancellationToken);
        if (sample is null) return (explicitTask, null, originalCorrelation);
        if (!string.Equals(sample.EquipmentCode, equipmentCode.Trim(), StringComparison.OrdinalIgnoreCase))
            return (null, null, "EquipmentMismatch");
        if (!string.IsNullOrWhiteSpace(explicitTaskId) &&
            (explicitTask is null || !string.Equals(sample.TaskUuid, explicitTaskId, StringComparison.Ordinal)))
            return (null, null, "TaskSampleMismatch");
        var channel = ReadString(body, "channel");
        if (channel is not null && !string.Equals(sample.Channel, channel, StringComparison.Ordinal))
            return (null, null, "ChannelMismatch");
        if (!sample.ConfigAttempted) return (null, null, "SampleNotDispatched");
        var task = explicitTask ?? await GetAsync(sample.TaskUuid, cancellationToken);
        return task is null ? (null, null, "UnknownTaskId") :
            (task, sample, string.IsNullOrWhiteSpace(explicitTaskId) ? "MesSampleId" : "ExplicitTaskIdAndSampleId");
    }

    private async Task ApplySampleObservationAsync(ShineLabTaskSample sample, ShineLabTaskRecord task,
        ShineLabPushEvidence evidence, JsonElement body, bool resolveConfigTimeout, CancellationToken cancellationToken)
    {
        sample.LastReceivedAtUtcTicks = evidence.ReceivedAtUtcTicks;
        switch (evidence.Method)
        {
            case "SampleFinish": sample.SampleFinishObserved = true; break;
            case "TaskFinish": sample.TaskFinishObserved = true; break;
            case "EndMission": sample.EndMissionObserved = true; break;
            case "Result":
                if (sample.ResultJson is not null &&
                    Canonicalize(JsonSerializer.Deserialize<JsonElement>(sample.ResultJson)) != Canonicalize(body))
                {
                    sample.ResultConflict = true;
                    evidence.Correlation = "SampleResultConflict";
                }
                else sample.ResultJson = body.GetRawText();
                break;
        }
        var samples = await GetSamplesAsync(task.TaskUuid, cancellationToken);
        var state = GetResultState(samples);
        if (resolveConfigTimeout && task.Status == "Unknown" &&
            evidence.Method is "SampleFinish" or "TaskFinish" or "EndMission" or "Result")
        {
            task.Status = "Observed";
            task.LastError = null;
            AddEvent(task.TaskUuid, "ConfigExecutionObservedWithoutAcknowledgement", new { sample.SampleId, evidence.Method });
        }
        // Never silently replace one sample's result with another's. Preserve
        // the single-sample compatibility property; multi-row detail is below.
        if (evidence.Method == "Result")
            task.ResultJson = samples.Count == 1 ? samples[0].ResultJson :
                JsonSerializer.Serialize(new { samples = samples.Select(item => new
                {
                    sampleId = item.SampleId, sourceSampleId = item.SourceSampleId,
                    result = item.ResultJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(item.ResultJson)
                }) });
        if (state == "NeedsReview") task.CurrentStage = "ResultConflictNeedsReview";
        else if (evidence.Method is not ("TaskError" or "AlarmInfo") && task.Status is not ("Error" or "Failed" or "Stopping" or "Unknown"))
        {
            if (state == "AllResultsReceived")
            {
                task.CurrentStage = "AllResultsReceivedAwaitingCompletion";
                task.Status = "AwaitingCompletion";
            }
            else if (state == "PartialResultsReceived") task.CurrentStage = "PartialResultsReceived";
        }
        // Results and finish names are observations, not proof of normal end.
        // CompletedAtUtc remains unchanged; errors/stop/unknown are not cleared.
    }
}
