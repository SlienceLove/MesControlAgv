using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using MesControlAgv.Simulator;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.E2E.Tests;

public sealed class TransportAcceptanceTests
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"mes-control-agv-e2e-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Normal_transport_requires_both_manual_confirmations_and_preserves_audit()
    {
        var adapter = new AcceptanceAdapter();
        var service = CreateService(adapter);

        var created = await service.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);
        await service.DispatchAsync(created.Id, CancellationToken.None);
        await service.RecordArrivalAsync(created.Id, CancellationToken.None);
        await service.ConfirmPickupAsync(created.Id, "operator-a", CancellationToken.None);
        await service.RecordArrivalAsync(created.Id, CancellationToken.None);
        var completed = await service.ConfirmDropoffAsync(created.Id, "operator-a", CancellationToken.None);
        var detail = await service.GetDetailAsync(created.Id, CancellationToken.None);

        Assert.Equal("Completed", completed.Status);
        Assert.Equal(["SAMPLE_01", "ST_PREP_01"], adapter.Targets);
        Assert.NotNull(detail);
        Assert.Contains(detail.Events, item => item.EventType == "PickupConfirmed");
        Assert.Contains(detail.Events, item => item.EventType == "DropoffConfirmed");
    }

    [Fact]
    public async Task Simulator_full_loop_covers_dispatch_pause_resume_arrivals_and_audit()
    {
        var simulator = new SimulatorState();
        var adapter = new SimulatorAcceptanceAdapter(simulator);
        var service = CreateService(adapter);

        var created = await service.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);
        Assert.Equal("Created", created.Status);

        var dispatched = await service.DispatchAsync(created.Id, CancellationToken.None);
        var pickupOperation = TransportOperationIds.Pickup(created.Id);
        Assert.Equal("MovingToPickup", dispatched.Status);
        Assert.Equal("moving", simulator.GetTask(pickupOperation)!.State);

        var pausedDevice = simulator.Pause(pickupOperation)!;
        var paused = await service.RecordAgvCommandAsync(
            pickupOperation,
            "pause",
            new AgvTaskResponse(pickupOperation, pickupOperation.ToString("N"), pausedDevice.TargetStationId, pausedDevice.State, pausedDevice.LastError),
            CancellationToken.None);
        Assert.Equal("Paused", paused?.Status);

        var resumedDevice = simulator.Resume(pickupOperation)!;
        var resumed = await service.RecordAgvCommandAsync(
            pickupOperation,
            "resume",
            new AgvTaskResponse(pickupOperation, pickupOperation.ToString("N"), resumedDevice.TargetStationId, resumedDevice.State, resumedDevice.LastError),
            CancellationToken.None);
        Assert.Equal("MovingToPickup", resumed?.Status);

        simulator.ApplyControl(pickupOperation, "arrive");
        var pickupWaiting = await service.RecordArrivalAsync(created.Id, CancellationToken.None);
        Assert.Equal("WaitingPickupConfirmation", pickupWaiting.Status);

        var dropoffMoving = await service.ConfirmPickupAsync(created.Id, "simulator-e2e", CancellationToken.None);
        var dropoffOperation = TransportOperationIds.Dropoff(created.Id);
        Assert.Equal("MovingToDropoff", dropoffMoving.Status);
        Assert.Equal("moving", simulator.GetTask(dropoffOperation)!.State);

        simulator.ApplyControl(dropoffOperation, "arrive");
        var dropoffWaiting = await service.RecordArrivalAsync(created.Id, CancellationToken.None);
        var completed = await service.ConfirmDropoffAsync(created.Id, "simulator-e2e", CancellationToken.None);

        Assert.Equal("WaitingDropoffConfirmation", dropoffWaiting.Status);
        Assert.Equal("Completed", completed.Status);
        var detail = await service.GetDetailAsync(created.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Contains(detail.Events, item => item.EventType == "TaskCreated");
        Assert.Contains(detail.Events, item => item.EventType == "DispatchRequested");
        Assert.Contains(detail.Events, item => item.EventType == "PauseRequested");
        Assert.Contains(detail.Events, item => item.EventType == "ResumeRequested");
        Assert.Contains(detail.Events, item => item.EventType == "PickupArrived");
        Assert.Contains(detail.Events, item => item.EventType == "PickupConfirmed");
        Assert.Contains(detail.Events, item => item.EventType == "DropoffArrived");
        Assert.Contains(detail.Events, item => item.EventType == "DropoffConfirmed");
    }

    [Fact]
    public async Task Simulator_task_cancellation_keeps_device_mes_and_fleet_idle_in_sync()
    {
        var simulator = new SimulatorState();
        var adapter = new SimulatorAcceptanceAdapter(simulator);
        var service = CreateService(adapter);

        var created = await service.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);
        var dispatched = await service.DispatchAsync(created.Id, CancellationToken.None);
        var operationId = TransportOperationIds.Pickup(created.Id);

        var cancelled = await service.CancelAsync(created.Id, "operator-cancel", CancellationToken.None);

        Assert.Equal("MovingToPickup", dispatched.Status);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal("cancelled", simulator.GetTask(operationId)?.State);
        Assert.Null(simulator.GetSnapshot("AGV-01").CurrentTaskId);
        var detail = await service.GetDetailAsync(created.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Contains(detail.Events, item => item.EventType == "CancelConfirmed");
    }

    [Fact]
    public async Task Consecutive_tasks_keep_separate_device_operations()
    {
        var adapter = new AcceptanceAdapter();
        var service = CreateService(adapter);

        var first = await service.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);
        await service.DispatchAsync(first.Id, CancellationToken.None);
        var second = await service.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);
        await service.DispatchAsync(second.Id, CancellationToken.None);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, adapter.OperationIds.Distinct().Count());
    }

    [Fact]
    public async Task Ten_sample_to_prep_tasks_complete_with_isolated_operations()
    {
        var adapter = new AcceptanceAdapter();
        var service = CreateService(adapter);

        for (var index = 0; index < 10; index++)
        {
            var created = await service.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);
            await service.DispatchAsync(created.Id, CancellationToken.None);
            await service.RecordArrivalAsync(created.Id, CancellationToken.None);
            await service.ConfirmPickupAsync(created.Id, "operator-a", CancellationToken.None);
            await service.RecordArrivalAsync(created.Id, CancellationToken.None);
            var completed = await service.ConfirmDropoffAsync(created.Id, "operator-a", CancellationToken.None);

            Assert.Equal("Completed", completed.Status);
        }

        Assert.Equal(20, adapter.OperationIds.Count);
        Assert.Equal(20, adapter.OperationIds.Distinct().Count());
        Assert.Equal(20, adapter.Targets.Count);
    }

    [Fact]
    public async Task Device_failure_can_retry_the_same_leg_and_records_retry_count()
    {
        var adapter = new AcceptanceAdapter { NextDispatchState = "failed", NextDispatchError = "blocked aisle" };
        var service = CreateService(adapter);

        var created = await service.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);
        var failed = await service.DispatchAsync(created.Id, CancellationToken.None);
        adapter.NextDispatchState = "moving";
        adapter.NextDispatchError = null;
        var retried = await service.RetryAsync(failed.Id, CancellationToken.None);

        Assert.Equal("Failed", failed.Status);
        Assert.Equal("blocked aisle", failed.LastError);
        Assert.Equal("MovingToPickup", retried.Status);
        Assert.Equal(1, retried.RetryCount);
        Assert.Equal(adapter.OperationIds[0], adapter.OperationIds[1]);
    }

    [Fact]
    public async Task Timeout_reconciles_to_device_state_without_a_second_dispatch()
    {
        var adapter = new AcceptanceAdapter { ThrowTimeout = true };
        var service = CreateService(adapter);

        var created = await service.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);
        var unknown = await service.DispatchAsync(created.Id, CancellationToken.None);
        adapter.ThrowTimeout = false;
        adapter.ReconciledState = "moving";
        var recovered = await service.RecoverAsync(unknown.Id, CancellationToken.None);

        Assert.Equal("Unknown", unknown.Status);
        Assert.Equal("AGV 响应超时，暂时无法确认设备状态。", unknown.LastError);
        Assert.Equal("MovingToPickup", recovered.Status);
        Assert.Single(adapter.OperationIds);
    }

    [Fact]
    public async Task Restart_reconciles_incomplete_task_from_persisted_sqlite_audit()
    {
        var adapter = new AcceptanceAdapter();
        var firstInstance = CreateService(adapter);
        var created = await firstInstance.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);
        await firstInstance.DispatchAsync(created.Id, CancellationToken.None);

        var restartedInstance = CreateService(adapter);
        await restartedInstance.ReconcileIncompleteAsync(CancellationToken.None);
        var detail = await restartedInstance.GetDetailAsync(created.Id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("MovingToPickup", detail.Task.Status);
        Assert.Contains(detail.Events, item => item.EventType == "Timeout");
        Assert.Contains(detail.Events, item => item.EventType == "ReconciledMoving");
    }

    private TaskService CreateService(IAgvGateway adapter)
    {
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;
        var context = new MesDbContext(options);
        context.Database.EnsureCreated();
        return new TaskService(new TaskRepository(context), adapter);
    }

}

internal sealed class AcceptanceAdapter : IAgvGateway
{
    private readonly Dictionary<Guid, AgvTaskResponse> _tasks = [];

    public List<Guid> OperationIds { get; } = [];
    public List<string> Targets { get; } = [];
    public string NextDispatchState { get; set; } = "moving";
    public string? NextDispatchError { get; set; }
    public bool ThrowTimeout { get; set; }
    public string? ReconciledState { get; set; }

    public Task<AgvTaskResponse> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken)
    {
        OperationIds.Add(operationId);
        Targets.Add(targetStationId);
        var task = new AgvTaskResponse(operationId, operationId.ToString("N"), targetStationId, NextDispatchState, NextDispatchError);
        _tasks[operationId] = task;
        if (ThrowTimeout) throw new TimeoutException("adapter timeout");
        return Task.FromResult(task);
    }

    public Task<AgvTaskResponse?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (ReconciledState is { } state && _tasks.TryGetValue(operationId, out var task))
        {
            return Task.FromResult<AgvTaskResponse?>(task with { State = state });
        }
        return Task.FromResult(_tasks.GetValueOrDefault(operationId));
    }

    public Task<AgvTaskResponse?> CancelAsync(Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult<AgvTaskResponse?>(new AgvTaskResponse(operationId, operationId.ToString("N"), string.Empty, "cancelled", null));

    public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AgvSnapshotResponse(true, "adapter", "CHARGE_01", null));

    public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(
        string agvId,
        string command,
        Guid? taskId,
        CancellationToken cancellationToken) =>
        Task.FromResult<AgvTaskResponse?>(null);
}

internal sealed class SimulatorAcceptanceAdapter(SimulatorState simulator) : IAgvGateway
{
    public Task<AgvTaskResponse> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken)
    {
        var task = simulator.Navigate(operationId, targetStationId);
        return Task.FromResult(new AgvTaskResponse(task.TaskId, task.TaskId.ToString("N"), task.TargetStationId, task.State, task.LastError, task.AgvId, task.Path));
    }

    public Task<AgvTaskResponse?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var task = simulator.GetTask(operationId);
        return Task.FromResult(task is null
            ? null
            : (AgvTaskResponse?)new AgvTaskResponse(task.TaskId, task.TaskId.ToString("N"), task.TargetStationId, task.State, task.LastError, task.AgvId, task.Path));
    }

    public Task<AgvTaskResponse?> CancelAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var task = simulator.Cancel(operationId);
        return Task.FromResult(task is null
            ? null
            : (AgvTaskResponse?)new AgvTaskResponse(task.TaskId, task.TaskId.ToString("N"), task.TargetStationId, task.State, task.LastError, task.AgvId, task.Path));
    }

    public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = simulator.GetSnapshot("AGV-01");
        return Task.FromResult(new AgvSnapshotResponse(snapshot.Online, snapshot.ControlOwner, snapshot.CurrentStationId, snapshot.CurrentTaskId, snapshot.AgvId));
    }

    public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken) =>
        Task.FromResult<AgvTaskResponse?>(null);
}


