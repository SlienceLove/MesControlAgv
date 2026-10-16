using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Services;

public sealed partial class ShineLabTaskRepository(
    MesDbContext database,
    IOptions<ShineLabTcpOptions>? configuredOptions = null)
{
    private bool IsDcSeq => configuredOptions?.Value.PayloadProfile is
        ShineLabPayloadProfile.DcSeq or ShineLabPayloadProfile.DcSeqSingleChannel;

    public async Task<(ShineLabTaskRecord Task, bool Created)> CreateOrGetAsync(
        ShineLabTaskCreateRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var config = new ShineLabConfigRequest(
            request.TaskUuid.Trim(),
            request.SampleData,
            request.InstrumentMethod,
            request.ProcessingMethod,
            request.DetectionMethod,
            request.Standard);
        var configJson = JsonSerializer.Serialize(config);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configJson)));
        var existing = await database.ShineLabTasks.SingleOrDefaultAsync(
            task => task.TaskUuid == request.TaskUuid.Trim(),
            cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.EquipmentCode, request.EquipmentCode.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"ShineLab task_uuid '{request.TaskUuid}' already exists with different task data.");
            return (existing, false);
        }

        var mappings = IsDcSeq
            ? await AllocateSamplesAsync(request, cancellationToken)
            : [];
        if (IsDcSeq)
        {
            config = config with { SampleData = request.SampleData.Select((row, index) =>
                row with { SampleId = mappings[index].SampleId }).ToArray() };
            // Validate before attaching anything; creation/preview never sends.
            ShineLabConfigPayloadBuilder.Build(config, configuredOptions!.Value.PayloadProfile);
            configJson = JsonSerializer.Serialize(config);
        }
        var now = DateTimeOffset.UtcNow;
        var task = new ShineLabTaskRecord
        {
            TaskUuid = request.TaskUuid.Trim(),
            EquipmentCode = request.EquipmentCode.Trim(),
            Status = "Created",
            CurrentStage = "Created",
            RequestFingerprint = fingerprint,
            ConfigJson = configJson,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.ShineLabTasks.Add(task);
        database.ShineLabTaskSamples.AddRange(mappings);
        AddEvent(task.TaskUuid, "TaskCreated", request);
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsDcSeq)
        {
            // SaveChanges is atomic on SQLite. A competing allocation or task
            // creation must never leak an unreserved ID into a Config send.
            throw new InvalidOperationException("ShineLab task/unique sampleId could not be reserved. No Config was sent; retry creation with the same taskUuid.", exception);
        }
        return (task, true);
    }

    public Task<ShineLabTaskRecord?> GetAsync(string taskUuid, CancellationToken cancellationToken) =>
        database.ShineLabTasks.SingleOrDefaultAsync(task => task.TaskUuid == taskUuid, cancellationToken);

    public async Task<List<ShineLabTaskRecord>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        var tasks = await database.ShineLabTasks
            .AsNoTracking()
            .Take(500)
            .ToListAsync(cancellationToken);
        return tasks
            .OrderByDescending(task => task.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();
    }

    public async Task<List<ShineLabTaskEventRecord>> GetEventsAsync(
        string taskUuid,
        CancellationToken cancellationToken)
    {
        var events = await database.ShineLabTaskEvents
            .AsNoTracking()
            .Where(taskEvent => taskEvent.TaskUuid == taskUuid)
            .ToListAsync(cancellationToken);
        return events.OrderBy(taskEvent => taskEvent.OccurredAtUtc).ToList();
    }

    public async Task<ShineLabTaskRecord> MarkConfigResultAsync(
        string taskUuid,
        ShineLabCommandResponse response,
        CancellationToken cancellationToken)
    {
        var task = await RequireAsync(taskUuid, cancellationToken);
        await database.Entry(task).ReloadAsync(cancellationToken);
        if (task.ConfigRequestId is not null && task.ConfigRequestId != response.StrId) return task;
        if (SameResponse(task.ConfigResponseJson, response)) return task;
        task.ConfigResponseJson = JsonSerializer.Serialize(response);
        var mayProject = !IsDcSeq || task.CommandRequestId is null &&
            (task.Status is "Created" or "Configuring" or "Configured" ||
             task.Status == "Unknown" && task.CurrentStage is "ConfigOutcomeUnknown" or "RecoveredAfterRestart");
        if (mayProject)
        {
            task.Status = response.Success ? "Configured" : "Failed";
            task.CurrentStage = response.Success
                ? IsDcSeq ? "ConfigAcceptedAwaitingVerification" : "Configured"
                : "ConfigRejected";
            task.LastError = response.Success ? null : response.Message;
            if (!response.Success) task.CompletedAtUtc = DateTimeOffset.UtcNow;
        }
        task.UpdatedAtUtc = DateTimeOffset.UtcNow;
        AddEvent(taskUuid, response.Success ? "ConfigAccepted" : "ConfigRejected", response);
        await database.SaveChangesAsync(cancellationToken);
        return task;
    }

    public async Task<ShineLabTaskRecord> MarkOperationStartedAsync(
        string taskUuid,
        string status,
        string stage,
        CancellationToken cancellationToken)
    {
        var task = await RequireAsync(taskUuid, cancellationToken);
        task.Status = status;
        task.CurrentStage = stage;
        task.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (IsDcSeq && status == "Configuring")
            foreach (var sample in await GetSamplesAsync(taskUuid, cancellationToken))
                sample.ConfigAttempted = true;
        AddEvent(taskUuid, stage, new { status, stage });
        await database.SaveChangesAsync(cancellationToken);
        return task;
    }

    public async Task<ShineLabTaskRecord> RestoreAfterNotConnectedAsync(
        string taskUuid,
        string status,
        string stage,
        string error,
        CancellationToken cancellationToken)
    {
        var task = await RequireAsync(taskUuid, cancellationToken);
        task.Status = status;
        task.CurrentStage = stage;
        task.LastError = error;
        task.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (IsDcSeq && stage == "WaitingForConnection")
            foreach (var sample in await GetSamplesAsync(taskUuid, cancellationToken))
                sample.ConfigAttempted = false;
        AddEvent(taskUuid, "NotSent", new { stage, error });
        await database.SaveChangesAsync(cancellationToken);
        return task;
    }

    public async Task<ShineLabTaskRecord> MarkCommandResultAsync(
        string taskUuid,
        ShineLabCommandRequest request,
        ShineLabCommandResponse response,
        CancellationToken cancellationToken)
    {
        var task = await RequireAsync(taskUuid, cancellationToken);
        await database.Entry(task).ReloadAsync(cancellationToken);
        if (task.CommandRequestId is not null && task.CommandRequestId != response.StrId) return task;
        if (SameResponse(task.CommandResponseJson, response)) return task;
        task.CommandResponseJson = JsonSerializer.Serialize(response);
        var samples = IsDcSeq ? await GetSamplesAsync(taskUuid, cancellationToken) : [];
        var mayProject = !IsDcSeq ||
            (task.Status is "Created" or "Configured" or "Commanding" ||
             task.Status == "Unknown" && task.CurrentStage is "CommandOutcomeUnknown" or "RecoveredAfterRestart") &&
            !(request.Action == 0 && samples.Any(s => s.ResultJson is not null || s.ResultConflict));
        if (mayProject)
        {
            task.Status = response.Success
                ? request.Action == 2 ? "Stopping" : "Accepted"
                : "Failed";
            task.CurrentStage = response.Success
                ? request.Action == 2 ? "Stopping" : "CommandAccepted"
                : "CommandRejected";
            task.LastError = response.Success ? null : response.Message;
            if (response.Success && request.Action == 0 && !IsDcSeq)
                task.StartedAtUtc ??= DateTimeOffset.UtcNow;
            // A newly accepted explicit start supersedes the prior rejected attempt's
            // end timestamp. Keep the rejection event/audit; do not invent a start time
            // or clear a newer terminal state (guarded by mayProject above).
            if (IsDcSeq && response.Success && request.Action == 0)
                task.CompletedAtUtc = null;
            if (!response.Success) task.CompletedAtUtc = DateTimeOffset.UtcNow;
        }
        task.UpdatedAtUtc = DateTimeOffset.UtcNow;
        AddEvent(taskUuid, response.Success ? "CommandAccepted" : "CommandRejected", new { request, response });
        await database.SaveChangesAsync(cancellationToken);
        return task;
    }

    public async Task<ShineLabTaskRecord> MarkOutcomeUnknownAsync(
        string taskUuid,
        string stage,
        string error,
        CancellationToken cancellationToken)
    {
        var task = await RequireAsync(taskUuid, cancellationToken);
        await database.Entry(task).ReloadAsync(cancellationToken);
        var preserve = IsDcSeq && (task.Status is "Error" or "Failed" or "Completed" or "Stopping" ||
            stage == "ConfigOutcomeUnknown" && task.Status == "Running");
        if (!preserve)
        {
            task.Status = "Unknown";
            task.CurrentStage = stage;
            task.LastError = error;
        }
        task.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (!preserve && IsDcSeq && stage == "ConfigOutcomeUnknown")
        {
            var samples = await GetSamplesAsync(taskUuid, cancellationToken);
            if (samples.Any(s => s.SampleFinishObserved || s.TaskFinishObserved || s.ResultJson is not null))
            {
                task.Status = "Observed";
                task.LastError = null;
                task.CurrentStage = GetResultState(samples) switch
                {
                    "AllResultsReceived" => "AllResultsReceivedAwaitingCompletion",
                    "PartialResultsReceived" => "PartialResultsReceived",
                    "NeedsReview" => "ResultConflictNeedsReview",
                    _ => "ConfigExecutionObservedWithoutAcknowledgement"
                };
                if (GetResultState(samples) == "AllResultsReceived") task.Status = "AwaitingCompletion";
            }
        }
        AddEvent(taskUuid, "OutcomeUnknown", new { stage, error });
        await database.SaveChangesAsync(cancellationToken);
        return task;
    }

    public async Task<int> MarkInterruptedTasksUnknownAsync(CancellationToken cancellationToken)
    {
        var statuses = new[] { "Configuring", "Commanding", "Accepted", "Running", "Stopping" };
        var tasks = await database.ShineLabTasks
            .Where(task => statuses.Contains(task.Status))
            .ToListAsync(cancellationToken);
        foreach (var task in tasks)
        {
            var previousStatus = task.Status;
            task.Status = "Unknown";
            task.CurrentStage = "RecoveredAfterRestart";
            task.LastError = "MES restarted while ShineLab task outcome was unresolved.";
            task.UpdatedAtUtc = DateTimeOffset.UtcNow;
            AddEvent(task.TaskUuid, "RecoveredAsUnknown", new { previousStatus });
        }
        if (tasks.Count > 0) await database.SaveChangesAsync(cancellationToken);
        return tasks.Count;
    }

    public async Task ApplyPushAsync(
        string strMethod,
        string equipmentCode,
        JsonElement body,
        CancellationToken cancellationToken,
        string? strId = null,
        ShineLabPushContext? context = null)
    {
        var taskUuid = ReadString(body, "task_uuid", "taskUuid", "taskId");
        var candidate = string.IsNullOrWhiteSpace(taskUuid) ? null :
            await database.ShineLabTasks.SingleOrDefaultAsync(
                item => item.TaskUuid == taskUuid, cancellationToken);
        var task = candidate is not null &&
            string.Equals(candidate.EquipmentCode, equipmentCode.Trim(), StringComparison.OrdinalIgnoreCase)
                ? candidate : null;

        if (IsDcSeq && strMethod is "SampleFinish" or "TaskFinish" or "EndMission" or "Result" or "TaskError" or "AlarmInfo")
        {
            var correlation = task is not null ? "ExplicitTaskId" : candidate is not null
                ? "EquipmentMismatch" : string.IsNullOrWhiteSpace(taskUuid)
                ? "MissingTaskId" : "UnknownTaskId";
            var resolution = await ResolveSampleAsync(equipmentCode, body, taskUuid, task, correlation, cancellationToken);
            task = resolution.Task;
            var sample = resolution.Sample;
            var evidence = new ShineLabPushEvidence
            {
                EquipmentCode = equipmentCode,
                StrId = strId ?? string.Empty,
                Method = strMethod,
                BodyJson = body.GetRawText(),
                RawJson = context?.RawJson,
                ConnectionId = context?.ConnectionId,
                WireFormat = context?.WireFormat,
                ReceivedAtUtcTicks = (context?.ReceivedAtUtc ?? DateTimeOffset.UtcNow).UtcTicks,
                TaskUuid = task?.TaskUuid,
                Correlation = resolution.Correlation,
                DeduplicationKey = GetEvidenceKey(strMethod, equipmentCode, body, strId)
            };
            evidence.IsDuplicate = evidence.DeduplicationKey is not null &&
                await database.ShineLabPushEvidence.AnyAsync(
                    item => item.DeduplicationKey == evidence.DeduplicationKey &&
                        item.TaskUuid == evidence.TaskUuid, cancellationToken);
            database.ShineLabPushEvidence.Add(evidence);
            // Always retain original frames, including duplicates and unmatched
            // baseline events. Only explicitly registered MES-owned sample IDs
            // can supplement the vendor's optional task_uuid.
            if (task is not null && !evidence.IsDuplicate)
            {
                var resolveConfigTimeout = task.Status == "Unknown" && task.CurrentStage == "ConfigOutcomeUnknown";
                if (strMethod == "Result" && sample is null) task.ResultJson = body.GetRawText();
                if (strMethod is "TaskError" or "AlarmInfo")
                {
                    task.Status = "Error";
                    task.LastError = ReadString(body, "errorMsg", "errorMessage", "msg");
                }
                task.CurrentStage = strMethod + "Observed";
                if (sample is not null)
                    await ApplySampleObservationAsync(sample, task, evidence, body, resolveConfigTimeout, cancellationToken);
                task.UpdatedAtUtc = DateTimeOffset.UtcNow;
                AddEvent(task.TaskUuid, evidence.Correlation == "SampleResultConflict" ? "ResultConflict" : strMethod + "Observed",
                    new { strId, equipmentCode, body, correlation = evidence.Correlation, sampleId = sample?.SampleId });
            }
            await database.SaveChangesAsync(cancellationToken);
            return;
        }
        if (task is null) return;

        var now = DateTimeOffset.UtcNow;
        var previousStatus = task.Status;
        var previousStage = task.CurrentStage;
        var previousError = task.LastError;
        var stage = ReadString(body, "stage", "lastKnownStage");
        var error = ReadString(body, "errorMsg", "errorMessage", "msg");
        switch (strMethod)
        {
            case "Device":
            case "UpdateInfo":
                var status = ReadInt(body, "status");
                if (status == 1)
                {
                    task.Status = "Running";
                    task.CurrentStage = stage ?? "Running";
                    task.StartedAtUtc ??= now;
                }
                else if (status == 2)
                {
                    task.Status = "Error";
                    task.CurrentStage = stage ?? "Error";
                    task.LastError = error;
                }
                break;

            case "AlarmInfo":
                task.CurrentStage = stage ?? "Alarm";
                task.LastError = error;
                break;

            case "SampleFinish":
                task.CurrentStage = "SampleFinished";
                break;

            case "Result":
                task.ResultJson = body.GetRawText();
                if (task.Status != "Completed") task.CurrentStage = "ResultAvailable";
                break;

            case "EndMission":
            case "TaskFinish":
                task.Status = "Completed";
                task.CurrentStage = "Completed";
                task.CompletedAtUtc = now;
                task.LastError = null;
                break;

            case "TaskError":
                task.Status = "Failed";
                task.CurrentStage = stage ?? "Failed";
                task.LastError = error ?? "ShineLab reported TaskError.";
                task.CompletedAtUtc = now;
                break;
        }

        if (strMethod == "UpdateInfo" &&
            task.Status == previousStatus &&
            task.CurrentStage == previousStage &&
            task.LastError == previousError)
        {
            return;
        }

        task.UpdatedAtUtc = now;
        AddEvent(task.TaskUuid, strMethod, new { strId, equipmentCode, body });
        await database.SaveChangesAsync(cancellationToken);
    }

    public Task<List<ShineLabPushEvidence>> GetPushEvidenceAsync(
        string equipmentCode, int limit, CancellationToken cancellationToken,
        string? taskUuid = null, string? method = null) =>
        database.ShineLabPushEvidence.AsNoTracking()
            .Where(item => item.EquipmentCode == equipmentCode)
            .Where(item => taskUuid == null || item.TaskUuid == taskUuid)
            .Where(item => method == null || item.Method == method)
            .OrderByDescending(item => item.ReceivedAtUtcTicks)
            .Take(Math.Clamp(limit, 1, 500)).ToListAsync(cancellationToken);

    private static string? GetEvidenceKey(string method, string equipmentCode, JsonElement body, string? strId)
    {
        // Paired events have different wire IDs. Result requires both a sample
        // ID and testDate; missing identifiers must not merge different runs.
        var semanticFinish = method is "SampleFinish" or "TaskFinish" or "EndMission" &&
            !string.IsNullOrWhiteSpace(ReadString(body, "finishDate"));
        var semanticResult = method == "Result" &&
            !string.IsNullOrWhiteSpace(ReadString(body, "sampleId", "sampleID")) &&
            !string.IsNullOrWhiteSpace(ReadString(body, "testDate"));
        if (!semanticFinish && !semanticResult && string.IsNullOrWhiteSpace(strId)) return null;
        var canonicalBody = Canonicalize(body);
        var input = JsonSerializer.Serialize(new { equipment = equipmentCode.Trim().ToUpperInvariant(),
            method, identity = semanticFinish ? "finish" : semanticResult ? "result" : strId, body = canonicalBody });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static string Canonicalize(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + Canonicalize(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonicalize)) + "]",
        _ => JsonSerializer.Serialize(value)
    };

    private static bool SameResponse(string? stored, ShineLabCommandResponse response) => stored is not null &&
        System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(stored),
            System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(response)));

    private async Task<ShineLabTaskRecord> RequireAsync(string taskUuid, CancellationToken cancellationToken) =>
        await GetAsync(taskUuid, cancellationToken)
        ?? throw new KeyNotFoundException($"ShineLab task '{taskUuid}' was not found.");

    private void AddEvent(string taskUuid, string eventType, object payload) =>
        database.ShineLabTaskEvents.Add(new ShineLabTaskEventRecord
        {
            TaskUuid = taskUuid,
            EventType = eventType,
            PayloadJson = JsonSerializer.Serialize(payload),
            OccurredAtUtc = DateTimeOffset.UtcNow
        });

    private static void Validate(ShineLabTaskCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.EquipmentCode))
            throw new ArgumentException("EquipmentCode is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TaskUuid))
            throw new ArgumentException("TaskUuid is required.", nameof(request));
        if (request.SampleData is null || request.SampleData.Count == 0)
            throw new ArgumentException("At least one sample row is required.", nameof(request));
    }

    private static string? ReadString(JsonElement body, params string[] names)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in body.EnumerateObject())
        {
            if (!names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase))) continue;
            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString();
        }
        return null;
    }

    private static int? ReadInt(JsonElement body, params string[] names) =>
        int.TryParse(ReadString(body, names), out var value) ? value : null;
}
