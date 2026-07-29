using System.Text.Json;
using MesControlAgv.Domain;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed class TaskRepository(MesDbContext database)
{
    public async Task<TransportTask> CreateAsync(
        int sourceStationCode,
        int targetStationCode,
        CancellationToken cancellationToken)
    {
        var task = new TransportTask
        {
            SourceStationCode = sourceStationCode,
            TargetStationCode = targetStationCode
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

    public Task<List<TransportTask>> ListAsync(CancellationToken cancellationToken) =>
        database.TransportTasks.OrderByDescending(task => task.UpdatedAt).ToListAsync(cancellationToken);

    public Task<List<TaskEventRecord>> GetEventsAsync(Guid taskId, CancellationToken cancellationToken) =>
        database.TaskEvents
            .Where(taskEvent => taskEvent.TaskId == taskId)
            .OrderBy(taskEvent => taskEvent.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<TransportTask> ApplyEventAsync(
        Guid taskId,
        TaskEvent taskEvent,
        object payload,
        CancellationToken cancellationToken)
    {
        var task = await GetAsync(taskId, cancellationToken)
            ?? throw new KeyNotFoundException($"Task {taskId} was not found.");

        task.Status = TaskStateMachine.Transition(task.Status, taskEvent);
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
