using MesControlAgv.Adapter.Contracts;
using MesControlAgv.Adapter.Data;
using MesControlAgv.Adapter.Entities;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;

namespace MesControlAgv.Adapter.Services;

public sealed class AdapterService(AdapterDbContext database, IAgvDeviceClient device)
{
    private static readonly object DispatchGatesLock = new();
    private static readonly Dictionary<Guid, DispatchGate> DispatchGates = new();

    public Task<AdapterTaskResponse> DispatchAsync(Guid taskId, string targetStationId, CancellationToken cancellationToken) =>
        DispatchAsync(taskId, null, targetStationId, cancellationToken);

    public async Task<AdapterTaskResponse> DispatchAsync(
        Guid taskId,
        string? sourceStationId,
        string targetStationId,
        CancellationToken cancellationToken)
    {
        var gate = AcquireDispatchGate(taskId);
        var acquired = false;
        var waited = false;
        try
        {
            if (gate.Semaphore.Wait(0))
            {
                acquired = true;
            }
            else
            {
                waited = true;
                await gate.Semaphore.WaitAsync(cancellationToken);
                acquired = true;
            }

            await device.EnsureControlAsync(cancellationToken);
            var snapshot = await device.GetSnapshotAsync(cancellationToken);
            if (!snapshot.Online || snapshot.ControlOwner != "adapter") throw new ControlUnavailableException(snapshot.ControlOwner);

            var existing = await database.Tasks.FindAsync([taskId], cancellationToken);
            if (existing is not null && (existing.State != "failed" || waited)) return ToResponse(existing);

            AdapterTaskResponse response;
            try { response = await device.NavigateAsync(taskId, sourceStationId, targetStationId, cancellationToken); }
            catch (TimeoutException)
            {
                response = await device.GetTaskAsync(taskId, cancellationToken)
                    ?? new AdapterTaskResponse(taskId, taskId.ToString("N"), targetStationId, "unknown", "timeout");
            }

            var task = new AdapterTask
            {
                TaskId = taskId,
                DeviceTaskId = response.DeviceTaskId,
                TargetStationId = targetStationId,
                State = response.State,
                LastError = response.LastError
            };
            if (existing is null)
            {
                database.Tasks.Add(task);
            }
            else
            {
                database.Entry(existing).State = EntityState.Detached;
                database.Tasks.Update(task);
            }
            await database.SaveChangesAsync(cancellationToken);
            return ToResponse(task);
        }
        finally
        {
            if (acquired) gate.Semaphore.Release();
            ReleaseDispatchGate(taskId, gate);
        }
    }

    private static DispatchGate AcquireDispatchGate(Guid taskId)
    {
        lock (DispatchGatesLock)
        {
            if (!DispatchGates.TryGetValue(taskId, out var gate))
            {
                gate = new DispatchGate();
                DispatchGates.Add(taskId, gate);
            }
            gate.References++;
            return gate;
        }
    }

    private static void ReleaseDispatchGate(Guid taskId, DispatchGate gate)
    {
        lock (DispatchGatesLock)
        {
            gate.References--;
            if (gate.References == 0) DispatchGates.Remove(taskId);
        }
    }

    private sealed class DispatchGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    public async Task<AdapterTaskResponse?> GetAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await database.Tasks.FindAsync([taskId], cancellationToken);
        if (task is null) return null;

        var deviceTask = await device.GetTaskAsync(taskId, cancellationToken);
        if (deviceTask is not null)
        {
            task.State = deviceTask.State;
            task.LastError = deviceTask.LastError;
            await database.SaveChangesAsync(cancellationToken);
        }

        return ToResponse(task);
    }

    public async Task<AdapterTaskResponse?> PauseAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await device.EnsureControlAsync(cancellationToken);
        var deviceTask = await device.PauseAsync(taskId, cancellationToken);
        return await PersistDeviceStateAsync(taskId, deviceTask, "paused", cancellationToken);
    }

    public async Task<AdapterTaskResponse?> ResumeAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await device.EnsureControlAsync(cancellationToken);
        var deviceTask = await device.ResumeAsync(taskId, cancellationToken);
        return await PersistDeviceStateAsync(taskId, deviceTask, "moving", cancellationToken);
    }

    public async Task<AdapterTaskResponse?> CancelAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await device.EnsureControlAsync(cancellationToken);
        var snapshot = await device.GetSnapshotAsync(cancellationToken);
        if (!snapshot.Online || snapshot.ControlOwner != "adapter") throw new ControlUnavailableException(snapshot.ControlOwner);

        var task = await database.Tasks.FindAsync([taskId], cancellationToken);
        if (task is null) return null;

        var deviceTask = await device.CancelAsync(taskId, cancellationToken);
        if (deviceTask?.State != "cancelled") throw new InvalidOperationException("Simulator did not confirm cancellation.");

        task.State = deviceTask.State;
        task.DeviceTaskId = deviceTask.DeviceTaskId;
        task.LastError = deviceTask.LastError;
        await database.SaveChangesAsync(cancellationToken);
        return ToResponse(task);
    }

    private async Task<AdapterTaskResponse?> PersistDeviceStateAsync(
        Guid taskId,
        AdapterTaskResponse? deviceTask,
        string fallbackState,
        CancellationToken cancellationToken)
    {
        var task = await database.Tasks.FindAsync([taskId], cancellationToken);
        if (task is null) return null;
        task.State = deviceTask?.State ?? fallbackState;
        task.DeviceTaskId = deviceTask?.DeviceTaskId ?? task.DeviceTaskId;
        task.LastError = deviceTask?.LastError;
        await database.SaveChangesAsync(cancellationToken);
        return ToResponse(task);
    }

    private static AdapterTaskResponse ToResponse(AdapterTask task) => new(task.TaskId, task.DeviceTaskId, task.TargetStationId, task.State, task.LastError);
}
