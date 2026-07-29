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

    public Task<AdapterTask> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken)
    {
        OperationIds.Add(operationId);
        LastTargetStationId = targetStationId;
        return Task.FromResult(new AdapterTask(operationId, operationId.ToString("N"), targetStationId, DispatchState, DispatchState == "failed" ? "failure" : null));
    }

    public Task<AdapterTask?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken) => Task.FromResult(Reconciled);
    public Task<AdapterTask?> CancelAsync(Guid operationId, CancellationToken cancellationToken) => Task.FromResult<AdapterTask?>(null);
    public Task<AdapterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new AdapterSnapshot(true, "adapter", null, null));
}
