using MesControlAgv.Mes.Contracts;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
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
    public async Task Consecutive_tasks_keep_separate_device_operations()
    {
        var adapter = new AcceptanceAdapter();
        var service = CreateService(adapter);

        var first = await service.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);
        var second = await service.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);

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

        var failed = await service.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);
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

        var unknown = await service.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);
        adapter.ThrowTimeout = false;
        adapter.ReconciledState = "moving";
        var recovered = await service.RecoverAsync(unknown.Id, CancellationToken.None);

        Assert.Equal("Unknown", unknown.Status);
        Assert.Equal("adapter timeout", unknown.LastError);
        Assert.Equal("MovingToPickup", recovered.Status);
        Assert.Single(adapter.OperationIds);
    }

    [Fact]
    public async Task Restart_reconciles_incomplete_task_from_persisted_sqlite_audit()
    {
        var adapter = new AcceptanceAdapter();
        var firstInstance = CreateService(adapter);
        var created = await firstInstance.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);

        var restartedInstance = CreateService(adapter);
        await restartedInstance.ReconcileIncompleteAsync(CancellationToken.None);
        var detail = await restartedInstance.GetDetailAsync(created.Id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("MovingToPickup", detail.Task.Status);
        Assert.Contains(detail.Events, item => item.EventType == "Timeout");
        Assert.Contains(detail.Events, item => item.EventType == "ReconciledMoving");
    }

    private TaskService CreateService(AcceptanceAdapter adapter)
    {
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;
        var context = new MesDbContext(options);
        context.Database.EnsureCreated();
        return new TaskService(new TaskRepository(context), adapter);
    }

}

internal sealed class AcceptanceAdapter : IAdapterClient
{
    private readonly Dictionary<Guid, AdapterTask> _tasks = [];

    public List<Guid> OperationIds { get; } = [];
    public List<string> Targets { get; } = [];
    public string NextDispatchState { get; set; } = "moving";
    public string? NextDispatchError { get; set; }
    public bool ThrowTimeout { get; set; }
    public string? ReconciledState { get; set; }

    public Task<AdapterTask> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken)
    {
        OperationIds.Add(operationId);
        Targets.Add(targetStationId);
        var task = new AdapterTask(operationId, operationId.ToString("N"), targetStationId, NextDispatchState, NextDispatchError);
        _tasks[operationId] = task;
        if (ThrowTimeout) throw new TimeoutException("adapter timeout");
        return Task.FromResult(task);
    }

    public Task<AdapterTask?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (ReconciledState is { } state && _tasks.TryGetValue(operationId, out var task))
        {
            return Task.FromResult<AdapterTask?>(task with { State = state });
        }
        return Task.FromResult(_tasks.GetValueOrDefault(operationId));
    }

    public Task<AdapterTask?> CancelAsync(Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult<AdapterTask?>(new AdapterTask(operationId, operationId.ToString("N"), string.Empty, "cancelled", null));

    public Task<AdapterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AdapterSnapshot(true, "adapter", "CHARGE_01", null));
}
