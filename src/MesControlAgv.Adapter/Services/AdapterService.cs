using System.Text.Json;
using MesControlAgv.Adapter.Contracts;
using MesControlAgv.Adapter.Data;
using MesControlAgv.Adapter.Entities;
using MesControlAgv.Domain;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Adapter.Services;

public sealed class AdapterService
{
    private static readonly object DispatchGatesLock = new();
    private static readonly Dictionary<Guid, DispatchGate> DispatchGates = new();

    private readonly AdapterDbContext _database;
    private readonly IAgvDeviceClient _device;
    private readonly IAgvFleetDeviceClient? _fleet;
    private readonly MultiAgvScheduler _scheduler;

    public AdapterService(
        AdapterDbContext database,
        IAgvDeviceClient device,
        IAgvFleetDeviceClient? fleet = null,
        MultiAgvScheduler? scheduler = null)
    {
        _database = database;
        _device = device;
        _fleet = fleet;
        _scheduler = scheduler ?? new MultiAgvScheduler(new PathPlanner(AgvMap.Default));
    }

    public Task<AdapterTaskResponse> DispatchAsync(Guid taskId, string targetStationId, CancellationToken cancellationToken) =>
        DispatchAsync(taskId, null, targetStationId, null, null, cancellationToken);

    public Task<AdapterTaskResponse> DispatchAsync(
        Guid taskId,
        string? sourceStationId,
        string targetStationId,
        CancellationToken cancellationToken) =>
        DispatchAsync(taskId, sourceStationId, targetStationId, null, null, cancellationToken);

    public async Task<AdapterTaskResponse> DispatchAsync(
        Guid taskId,
        string? sourceStationId,
        string targetStationId,
        string? requestedAgvId,
        IReadOnlyList<string>? requestedPath,
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

            var existing = await _database.Tasks.FindAsync([taskId], cancellationToken);
            if (existing is not null && (existing.State != "failed" || waited))
            {
                await _device.EnsureControlAsync(cancellationToken);
                var existingSnapshot = await GetSnapshotAsync(existing.AgvId, cancellationToken);
                if (!existingSnapshot.Online || existingSnapshot.ControlOwner != "adapter")
                {
                    throw new ControlUnavailableException(existingSnapshot.ControlOwner);
                }
                return ToResponse(existing);
            }

            var assignment = await SelectAgvAsync(taskId, sourceStationId, targetStationId, requestedAgvId, cancellationToken);
            await _device.EnsureControlAsync(cancellationToken);
            var snapshot = await GetSnapshotAsync(assignment.AgvId, cancellationToken);
            if (!snapshot.Online || snapshot.ControlOwner != "adapter") throw new ControlUnavailableException(snapshot.ControlOwner);

            var path = requestedPath ?? assignment.Path?.Stations;
            AdapterTaskResponse response;
            try
            {
                response = _fleet is not null
                    ? await _fleet.NavigateAsync(assignment.AgvId, taskId, sourceStationId, targetStationId, path, cancellationToken)
                    : await _device.NavigateAsync(taskId, sourceStationId, targetStationId, cancellationToken);
                response = response with { AgvId = assignment.AgvId, Path = path };
            }
            catch (TimeoutException)
            {
                response = await GetTaskFromDeviceAsync(assignment.AgvId, taskId, cancellationToken)
                    ?? new AdapterTaskResponse(taskId, taskId.ToString("N"), targetStationId, "unknown", "timeout", assignment.AgvId, path);
            }

            var task = new AdapterTask
            {
                TaskId = taskId,
                AgvId = assignment.AgvId,
                DeviceTaskId = response.DeviceTaskId,
                TargetStationId = targetStationId,
                State = response.State,
                LastError = response.LastError,
                PathJson = path is null ? null : JsonSerializer.Serialize(path)
            };
            if (existing is null)
            {
                _database.Tasks.Add(task);
            }
            else
            {
                _database.Entry(existing).State = EntityState.Detached;
                _database.Tasks.Update(task);
            }
            await _database.SaveChangesAsync(cancellationToken);
            return ToResponse(task);
        }
        finally
        {
            if (acquired) gate.Semaphore.Release();
            ReleaseDispatchGate(taskId, gate);
        }
    }

    public async Task<AdapterTaskResponse?> GetAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await _database.Tasks.FindAsync([taskId], cancellationToken);
        if (task is null) return null;

        var deviceTask = await GetTaskFromDeviceAsync(task.AgvId, taskId, cancellationToken);
        if (deviceTask is not null)
        {
            task.State = deviceTask.State;
            task.LastError = deviceTask.LastError;
            await _database.SaveChangesAsync(cancellationToken);
            ReleaseCompletedRoute(taskId, deviceTask.State);
        }

        return ToResponse(task);
    }

    public async Task<IReadOnlyList<AgvSnapshotResponse>> GetFleetAsync(CancellationToken cancellationToken)
    {
        if (_fleet is not null) return await _fleet.GetFleetSnapshotAsync(cancellationToken);
        return [await _device.GetSnapshotAsync(cancellationToken)];
    }

    public async Task<AdapterTaskResponse?> ExecuteCommandAsync(
        string agvId,
        string command,
        Guid? requestedTaskId,
        CancellationToken cancellationToken)
    {
        var snapshots = await GetFleetAsync(cancellationToken);
        var snapshot = snapshots.SingleOrDefault(item => StringComparer.Ordinal.Equals(item.AgvId, agvId))
            ?? throw new KeyNotFoundException($"AGV {agvId} is not configured.");
        var taskId = requestedTaskId ?? snapshot.CurrentTaskId
            ?? throw new InvalidOperationException($"AGV {agvId} has no active task.");

        return command.Trim().ToLowerInvariant() switch
        {
            "pause" or "stop" => await PauseAsync(taskId, cancellationToken),
            "resume" or "continue" => await ResumeAsync(taskId, cancellationToken),
            "cancel" => await CancelAsync(taskId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Supported commands: pause, resume, cancel.")
        };
    }

    public async Task<AdapterTaskResponse?> PauseAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await _database.Tasks.FindAsync([taskId], cancellationToken);
        if (task is null) return null;
        await _device.EnsureControlAsync(cancellationToken);
        var deviceTask = _fleet is not null
            ? await _fleet.PauseAsync(task.AgvId, taskId, cancellationToken)
            : await _device.PauseAsync(taskId, cancellationToken);
        return await PersistDeviceStateAsync(taskId, deviceTask, "paused", cancellationToken);
    }

    public async Task<AdapterTaskResponse?> ResumeAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await _database.Tasks.FindAsync([taskId], cancellationToken);
        if (task is null) return null;
        await _device.EnsureControlAsync(cancellationToken);
        var deviceTask = _fleet is not null
            ? await _fleet.ResumeAsync(task.AgvId, taskId, cancellationToken)
            : await _device.ResumeAsync(taskId, cancellationToken);
        return await PersistDeviceStateAsync(taskId, deviceTask, "moving", cancellationToken);
    }

    public async Task<AdapterTaskResponse?> CancelAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await _device.EnsureControlAsync(cancellationToken);
        var task = await _database.Tasks.FindAsync([taskId], cancellationToken);
        if (task is null) return null;

        var snapshot = await GetSnapshotAsync(task.AgvId, cancellationToken);
        if (!snapshot.Online || snapshot.ControlOwner != "adapter") throw new ControlUnavailableException(snapshot.ControlOwner);

        var deviceTask = _fleet is not null
            ? await _fleet.CancelAsync(task.AgvId, taskId, cancellationToken)
            : await _device.CancelAsync(taskId, cancellationToken);
        if (deviceTask?.State != "cancelled") throw new InvalidOperationException("Simulator did not confirm cancellation.");

        task.State = deviceTask.State;
        task.DeviceTaskId = deviceTask.DeviceTaskId;
        task.LastError = deviceTask.LastError;
        await _database.SaveChangesAsync(cancellationToken);
        _scheduler.Release(taskId);
        return ToResponse(task);
    }

    private async Task<AdapterTaskResponse?> PersistDeviceStateAsync(
        Guid taskId,
        AdapterTaskResponse? deviceTask,
        string fallbackState,
        CancellationToken cancellationToken)
    {
        var task = await _database.Tasks.FindAsync([taskId], cancellationToken);
        if (task is null) return null;
        task.State = deviceTask?.State ?? fallbackState;
        task.DeviceTaskId = deviceTask?.DeviceTaskId ?? task.DeviceTaskId;
        task.LastError = deviceTask?.LastError;
        await _database.SaveChangesAsync(cancellationToken);
        return ToResponse(task);
    }

    private async Task<(string AgvId, PlannedPath? Path)> SelectAgvAsync(
        Guid taskId,
        string? sourceStationId,
        string targetStationId,
        string? requestedAgvId,
        CancellationToken cancellationToken)
    {
        if (_fleet is null) return (requestedAgvId ?? "AGV-01", null);

        var snapshots = await _fleet.GetFleetSnapshotAsync(cancellationToken);
        _scheduler.ReleaseForIdleAgvs(snapshots.Where(snapshot => snapshot.CurrentTaskId is null)
            .Select(snapshot => snapshot.AgvId)
            .ToHashSet(StringComparer.Ordinal));

        var candidates = snapshots
            .Where(snapshot => requestedAgvId is null || StringComparer.Ordinal.Equals(snapshot.AgvId, requestedAgvId))
            .Select(snapshot => new AgvCandidate(
                snapshot.AgvId,
                snapshot.Online,
                snapshot.ControlOwner,
                snapshot.CurrentStationId ?? "CHARGE_01",
                snapshot.CurrentTaskId is not null))
            .ToArray();
        var decision = _scheduler.Schedule(
            taskId,
            sourceStationId ?? "CHARGE_01",
            targetStationId,
            candidates);
        if (!decision.Assigned || decision.AgvId is null)
        {
            throw new AgvUnavailableException(decision.Reason ?? "No AGV can accept the route.");
        }

        return (decision.AgvId, decision.Path);
    }

    private async Task<AgvSnapshotResponse> GetSnapshotAsync(string agvId, CancellationToken cancellationToken)
    {
        if (_fleet is not null)
        {
            var snapshot = (await _fleet.GetFleetSnapshotAsync(cancellationToken))
                .SingleOrDefault(item => StringComparer.Ordinal.Equals(item.AgvId, agvId));
            return snapshot ?? throw new KeyNotFoundException($"AGV {agvId} was not found.");
        }

        return await _device.GetSnapshotAsync(cancellationToken);
    }

    private async Task<AdapterTaskResponse?> GetTaskFromDeviceAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        _fleet is not null
            ? await _fleet.GetTaskAsync(agvId, taskId, cancellationToken)
            : await _device.GetTaskAsync(taskId, cancellationToken);

    private void ReleaseCompletedRoute(Guid taskId, string state)
    {
        if (state is "arrived" or "completed" or "cancelled" or "failed") _scheduler.Release(taskId);
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

    private static AdapterTaskResponse ToResponse(AdapterTask task) => new(
        task.TaskId,
        task.DeviceTaskId,
        task.TargetStationId,
        task.State,
        task.LastError,
        task.AgvId,
        DeserializePath(task.PathJson));

    private static IReadOnlyList<string>? DeserializePath(string? pathJson) =>
        pathJson is null ? null : JsonSerializer.Deserialize<IReadOnlyList<string>>(pathJson);
}

public sealed class AgvUnavailableException(string reason)
    : InvalidOperationException(reason);
