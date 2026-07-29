using MesControlAgv.Adapter.Contracts;
using MesControlAgv.Adapter.Data;
using MesControlAgv.Adapter.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Adapter.Services;

public sealed class AdapterService(AdapterDbContext database, ISimulatorClient simulator)
{
    public async Task<AdapterTaskResponse> DispatchAsync(Guid taskId, string targetStationId, CancellationToken cancellationToken)
    {
        var existing = await database.Tasks.FindAsync([taskId], cancellationToken);
        if (existing is not null) return ToResponse(existing);

        var snapshot = await simulator.GetSnapshotAsync(cancellationToken);
        if (!snapshot.Online || snapshot.ControlOwner != "adapter") throw new ControlUnavailableException(snapshot.ControlOwner);

        AdapterTaskResponse response;
        try { response = await simulator.NavigateAsync(taskId, targetStationId, cancellationToken); }
        catch (TimeoutException)
        {
            response = await simulator.GetTaskAsync(taskId, cancellationToken)
                ?? new AdapterTaskResponse(taskId, taskId.ToString("N"), targetStationId, "unknown", "timeout");
        }

        var task = new AdapterTask { TaskId = taskId, DeviceTaskId = response.DeviceTaskId, TargetStationId = targetStationId, State = response.State, LastError = response.LastError };
        database.Tasks.Add(task);
        await database.SaveChangesAsync(cancellationToken);
        return ToResponse(task);
    }

    public async Task<AdapterTaskResponse?> GetAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await database.Tasks.FindAsync([taskId], cancellationToken);
        return task is null ? null : ToResponse(task);
    }

    public async Task<AdapterTaskResponse?> PauseAsync(Guid taskId, CancellationToken cancellationToken) =>
        await UpdateStateAsync(taskId, "paused", cancellationToken);

    public async Task<AdapterTaskResponse?> ResumeAsync(Guid taskId, CancellationToken cancellationToken) =>
        await UpdateStateAsync(taskId, "moving", cancellationToken);

    public async Task<AdapterTaskResponse?> CancelAsync(Guid taskId, CancellationToken cancellationToken) =>
        await UpdateStateAsync(taskId, "cancelled", cancellationToken);

    private async Task<AdapterTaskResponse?> UpdateStateAsync(Guid taskId, string state, CancellationToken cancellationToken)
    {
        var task = await database.Tasks.FindAsync([taskId], cancellationToken);
        if (task is null) return null;
        task.State = state;
        await database.SaveChangesAsync(cancellationToken);
        return ToResponse(task);
    }

    private static AdapterTaskResponse ToResponse(AdapterTask task) => new(task.TaskId, task.DeviceTaskId, task.TargetStationId, task.State, task.LastError);
}
