using MesControlAgv.Domain;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Tests;

public class TransportWorkflowTests
{
    [Fact]
    public async Task Pickup_confirmation_dispatches_dropoff()
    {
        var adapter = new FakeAdapterClient();
        var service = CreateService(adapter);

        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.RecordArrivalAsync(task.Id, CancellationToken.None);
        var updated = await service.ConfirmPickupAsync(task.Id, "operator", CancellationToken.None);

        Assert.Equal("MovingToDropoff", updated.Status);
        Assert.Equal("ST_PREP_01", adapter.LastTargetStationId);
    }

    [Fact]
    public async Task Failed_task_retries_the_same_pickup_operation_id()
    {
        var adapter = new FakeAdapterClient { DispatchState = "failed" };
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        adapter.DispatchState = "moving";

        var retried = await service.RetryAsync(task.Id, CancellationToken.None);

        Assert.Equal("MovingToPickup", retried.Status);
        Assert.Equal(adapter.OperationIds[0], adapter.OperationIds[1]);
    }

    [Fact]
    public async Task Retry_on_a_non_failed_task_does_not_change_task_or_events()
    {
        var adapter = new FakeAdapterClient { ThrowTimeout = true };
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        var before = await service.GetDetailAsync(task.Id, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidTaskTransitionException>(() => service.RetryAsync(task.Id, CancellationToken.None));

        var after = await service.GetDetailAsync(task.Id, CancellationToken.None);
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before.Task, after.Task);
        Assert.Equal(before.Events.Count, after.Events.Count);
        Assert.Equal(before.Events.Select(item => item.EventType), after.Events.Select(item => item.EventType));
    }

    [Fact]
    public async Task Cancellation_requires_adapter_device_confirmation()
    {
        var adapter = new FakeAdapterClient { CancelState = "moving" };
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(task.Id, "operator", CancellationToken.None));

        var detail = await service.GetDetailAsync(task.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal("MovingToPickup", detail.Task.Status);
        Assert.DoesNotContain(detail.Events, item => item.EventType == "CancelConfirmed");
    }

    [Fact]
    public async Task Confirmed_adapter_cancellation_records_cancel_confirmed()
    {
        var adapter = new FakeAdapterClient { CancelState = "cancelled" };
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);

        var cancelled = await service.CancelAsync(task.Id, "operator", CancellationToken.None);

        Assert.Equal("Cancelled", cancelled.Status);
    }

    [Fact]
    public async Task Recovery_maps_arrived_pickup_to_operator_confirmation()
    {
        var adapter = new FakeAdapterClient();
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.MarkUnknownAsync(task.Id, CancellationToken.None);
        adapter.Reconciled = new(TransportOperationIds.Pickup(task.Id), "device", "SAMPLE_01", "arrived", null);

        var recovered = await service.RecoverAsync(task.Id, CancellationToken.None);

        Assert.Equal("WaitingPickupConfirmation", recovered.Status);
    }

    [Fact]
    public async Task Startup_recovery_retries_adapter_unavailability_and_leaves_task_unknown()
    {
        var adapter = new FakeAdapterClient { ThrowGetHttpRequest = true };
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);

        await service.ReconcileIncompleteAsync(CancellationToken.None);

        var detail = await service.GetDetailAsync(task.Id, CancellationToken.None);
        Assert.Equal(3, adapter.GetTaskCalls);
        Assert.Equal("Unknown", detail!.Task.Status);
        Assert.Contains(detail.Events, item => item.EventType == "Timeout");
        Assert.DoesNotContain(detail.Events, item => item.EventType.StartsWith("Reconciled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Startup_recovery_retries_transport_cancellation_and_keeps_mes_available()
    {
        var adapter = new FakeAdapterClient { ThrowGetTaskCancellation = true };
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);

        await service.ReconcileIncompleteAsync(CancellationToken.None);

        var detail = await service.GetDetailAsync(task.Id, CancellationToken.None);
        Assert.Equal(3, adapter.GetTaskCalls);
        Assert.Equal("Unknown", detail!.Task.Status);
    }

    [Fact]
    public async Task Startup_recovery_does_not_swallow_stopping_cancellation()
    {
        using var stopping = new CancellationTokenSource();
        var adapter = new FakeAdapterClient { CancelOnGet = stopping };
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReconcileIncompleteAsync(stopping.Token));
        Assert.NotEqual(Guid.Empty, task.Id);
    }

    private static TaskService CreateService(FakeAdapterClient adapter)
    {
        var options = new DbContextOptionsBuilder<MesDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new TaskService(new TaskRepository(new MesDbContext(options)), adapter);
    }
}

internal sealed class FakeAdapterClient : IAdapterClient
{
    public string DispatchState { get; set; } = "moving";
    public string? LastTargetStationId { get; private set; }
    public List<Guid> OperationIds { get; } = [];
    public AdapterTask? Reconciled { get; set; }
    public string? CancelState { get; set; }
    public bool ThrowTimeout { get; set; }
    public bool ThrowGetHttpRequest { get; set; }
    public bool ThrowGetTaskCancellation { get; set; }
    public int GetTaskCalls { get; private set; }
    public CancellationTokenSource? CancelOnGet { get; set; }

    public Task<AdapterTask> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken)
    {
        OperationIds.Add(operationId);
        LastTargetStationId = targetStationId;
        var task = new AdapterTask(operationId, operationId.ToString("N"), targetStationId, DispatchState, DispatchState == "failed" ? "failure" : null);
        return ThrowTimeout
            ? Task.FromException<AdapterTask>(new TimeoutException("adapter timeout"))
            : Task.FromResult(task);
    }

    public Task<AdapterTask?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken)
    {
        GetTaskCalls++;
        if (CancelOnGet is not null)
        {
            CancelOnGet.Cancel();
            return Task.FromException<AdapterTask?>(new TaskCanceledException("adapter request cancelled"));
        }
        if (ThrowGetHttpRequest) return Task.FromException<AdapterTask?>(new HttpRequestException("adapter unavailable"));
        if (ThrowGetTaskCancellation) return Task.FromException<AdapterTask?>(new TaskCanceledException("adapter request timed out"));
        return Task.FromResult(Reconciled);
    }

    public Task<AdapterTask?> CancelAsync(Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult<AdapterTask?>(CancelState is null
            ? null
            : new AdapterTask(operationId, operationId.ToString("N"), "SAMPLE_01", CancelState, null));
    public Task<AdapterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new AdapterSnapshot(true, "adapter", null, null));
}
