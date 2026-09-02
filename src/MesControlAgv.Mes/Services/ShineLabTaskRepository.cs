using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed class ShineLabTaskRepository(MesDbContext database)
{
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
            request.DetectionMethod);
        var configJson = JsonSerializer.Serialize(config);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configJson)));
        var existing = await database.ShineLabTasks.SingleOrDefaultAsync(
            task => task.TaskUuid == request.TaskUuid,
            cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"ShineLab task_uuid '{request.TaskUuid}' already exists with different task data.");
            return (existing, false);
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
        AddEvent(task.TaskUuid, "TaskCreated", request);
        await database.SaveChangesAsync(cancellationToken);
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
        task.ConfigResponseJson = JsonSerializer.Serialize(response);
        task.Status = response.Success ? "Configured" : "Failed";
        task.CurrentStage = response.Success ? "Configured" : "ConfigRejected";
        task.LastError = response.Success ? null : response.Message;
        task.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (!response.Success) task.CompletedAtUtc = task.UpdatedAtUtc;
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
        task.CommandResponseJson = JsonSerializer.Serialize(response);
        task.Status = response.Success
            ? request.Action == 1 ? "Stopping" : "Accepted"
            : "Failed";
        task.CurrentStage = response.Success
            ? request.Action == 1 ? "Stopping" : "CommandAccepted"
            : "CommandRejected";
        task.LastError = response.Success ? null : response.Message;
        task.StartedAtUtc ??= response.Success ? DateTimeOffset.UtcNow : null;
        task.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (!response.Success) task.CompletedAtUtc = task.UpdatedAtUtc;
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
        task.Status = "Unknown";
        task.CurrentStage = stage;
        task.LastError = error;
        task.UpdatedAtUtc = DateTimeOffset.UtcNow;
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
        CancellationToken cancellationToken)
    {
        var taskUuid = ReadString(body, "task_uuid", "taskUuid", "taskId");
        if (string.IsNullOrWhiteSpace(taskUuid)) return;
        var task = await database.ShineLabTasks.SingleOrDefaultAsync(
            item => item.TaskUuid == taskUuid,
            cancellationToken);
        if (task is null) return;

        var now = DateTimeOffset.UtcNow;
        var previousStatus = task.Status;
        var previousStage = task.CurrentStage;
        var previousError = task.LastError;
        var stage = ReadString(body, "stage", "lastKnownStage");
        var error = ReadString(body, "errorMsg", "errorMessage", "msg");
        switch (strMethod)
        {
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
        AddEvent(taskUuid, strMethod, new { equipmentCode, body });
        await database.SaveChangesAsync(cancellationToken);
    }

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
