using System.Text.Json;
using MesControlAgv.Domain;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed class TaskRepository(MesDbContext database)
{
    public Task<TransportTask> CreateAsync(int sourceStationCode, int targetStationCode, CancellationToken cancellationToken) =>
        CreateAsync(sourceStationCode, targetStationCode, 0, null, null, cancellationToken);
    public async Task<TransportTask> CreateAsync(
        int sourceStationCode,
        int targetStationCode,
        int priority,
        string? description,
        string? externalId,
        CancellationToken cancellationToken)
    {
        var task = new TransportTask
        {
            SourceStationCode = sourceStationCode,
            TargetStationCode = targetStationCode,
            Priority = priority,
            Description = description,
            ExternalId = externalId
        };

        database.TransportTasks.Add(task);
        database.TaskEvents.Add(new TaskEventRecord
        {
            TaskId = task.Id,
            EventType = "TaskCreated",
            Payload = JsonSerializer.Serialize(new { sourceStationCode, targetStationCode })
        });
        await database.SaveChangesAsync(cancellationToken);

        return task;
    }

    public Task<TransportTask?> GetAsync(Guid taskId, CancellationToken cancellationToken) =>
        database.TransportTasks.SingleOrDefaultAsync(task => task.Id == taskId, cancellationToken);

    public Task<TransportTask?> GetByActiveOperationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var deviceTaskId = operationId.ToString("N");
        return database.TransportTasks.SingleOrDefaultAsync(
            task => task.ActiveDeviceTaskId == deviceTaskId,
            cancellationToken);
    }

    public Task<List<TransportTask>> ListAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var start = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = start.AddDays(1);
        return database.TransportTasks
            .Where(task => task.CreatedAt >= start && task.CreatedAt < end)
            .OrderByDescending(task => task.Priority)
            .ThenBy(task => task.CreatedAt)
            .ThenByDescending(task => task.UpdatedAt)
            .ToListAsync(cancellationToken);
    }

    public Task<List<TransportTask>> ListAsync(CancellationToken cancellationToken) =>
        ListAsync(DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken);

    public Task<List<TaskEventRecord>> GetEventsAsync(Guid taskId, CancellationToken cancellationToken) =>
        database.TaskEvents
            .Where(taskEvent => taskEvent.TaskId == taskId)
            .OrderBy(taskEvent => taskEvent.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<List<TransportTask>> ListByStatusAsync(MesControlAgv.Domain.TaskStatus status, CancellationToken cancellationToken) =>
        database.TransportTasks.Where(task => task.Status == status).ToListAsync(cancellationToken);

    public async Task<TransportTask> SetActiveTargetAsync(
        Guid taskId,
        string targetStationId,
        CancellationToken cancellationToken)
        => await SetActiveRouteAsync(taskId, targetStationId, null, null, null, cancellationToken);

    public async Task<TransportTask> SetActiveRouteAsync(
        Guid taskId,
        string targetStationId,
        string? agvId,
        string? deviceTaskId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken)
    {
        var task = await GetAsync(taskId, cancellationToken)
            ?? throw new KeyNotFoundException($"Task {taskId} was not found.");
        task.ActiveTargetStationId = targetStationId;
        task.ActiveAgvId = agvId;
        task.ActiveDeviceTaskId = deviceTaskId;
        task.ActivePathJson = path is null ? null : JsonSerializer.Serialize(path);
        task.UpdatedAt = DateTime.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return task;
    }

    public async Task RecordEventAsync(
        Guid taskId,
        string eventType,
        object payload,
        CancellationToken cancellationToken)
    {
        if (!await database.TransportTasks.AnyAsync(task => task.Id == taskId, cancellationToken))
        {
            throw new KeyNotFoundException($"Task {taskId} was not found.");
        }

        database.TaskEvents.Add(new TaskEventRecord
        {
            TaskId = taskId,
            EventType = eventType,
            Payload = JsonSerializer.Serialize(payload)
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<TransportTask> IncrementRetryAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await GetAsync(taskId, cancellationToken)
            ?? throw new KeyNotFoundException($"Task {taskId} was not found.");
        if (task.Status != MesControlAgv.Domain.TaskStatus.Failed)
        {
            throw new InvalidTaskTransitionException(task.Status, TaskEvent.RetryRequested);
        }
        task.RetryCount++;
        task.LastError = null;
        task.EndedAt = null;
        task.UpdatedAt = DateTime.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return task;
    }

    public async Task<TransportTask> ApplyEventAsync(
        Guid taskId,
        TaskEvent taskEvent,
        object payload,
        CancellationToken cancellationToken,
        string? error = null)
    {
        var task = await GetAsync(taskId, cancellationToken)
            ?? throw new KeyNotFoundException($"Task {taskId} was not found.");

        task.Status = TaskStateMachine.Transition(task.Status, taskEvent);
        if (taskEvent is TaskEvent.DeviceFailed or TaskEvent.Timeout)
        {
            task.LastError = error;
        }

        if (task.Status is MesControlAgv.Domain.TaskStatus.Completed
            or MesControlAgv.Domain.TaskStatus.Cancelled
            or MesControlAgv.Domain.TaskStatus.Failed)
        {
            task.EndedAt = DateTime.UtcNow;
        }
        else if (taskEvent == TaskEvent.RetryRequested)
        {
            task.EndedAt = null;
        }

        task.UpdatedAt = DateTime.UtcNow;
        database.TaskEvents.Add(new TaskEventRecord
        {
            TaskId = task.Id,
            EventType = taskEvent.ToString(),
            Payload = JsonSerializer.Serialize(payload)
        });
        await database.SaveChangesAsync(cancellationToken);

        return task;
    }
}
