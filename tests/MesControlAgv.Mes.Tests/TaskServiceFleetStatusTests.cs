using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Tests;

public sealed class TaskServiceFleetStatusTests
{
    [Fact]
    public async Task Fleet_status_prefers_the_snapshot_operation_id_over_a_stale_newest_task()
    {
        var adapter = new FleetStatusAdapter();
        var service = CreateService(adapter);

        var stale = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(stale.Id, CancellationToken.None);
        var current = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(current.Id, CancellationToken.None);

        adapter.Snapshots =
        [
            new AgvSnapshotResponse(
                Online: true,
                ControlOwner: "adapter",
                CurrentStationId: "CHARGE_01",
                CurrentTaskId: TransportOperationIds.Pickup(current.Id),
                AgvId: "AGV-01")
        ];

        var status = Assert.Single(await service.GetFleetStatusAsync(CancellationToken.None));

        Assert.NotNull(status.ActiveTask);
        Assert.Equal(current.Id, status.ActiveTask.TransportTaskId);
        Assert.Equal(TransportOperationIds.Pickup(current.Id), status.ActiveTask.OperationId);
    }

    [Fact]
    public async Task Fleet_status_does_not_guess_when_multiple_active_tasks_have_no_device_correlation()
    {
        var adapter = new FleetStatusAdapter();
        var service = CreateService(adapter);

        var first = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(first.Id, CancellationToken.None);
        var second = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(second.Id, CancellationToken.None);

        adapter.Snapshots =
        [
            new AgvSnapshotResponse(
                Online: true,
                ControlOwner: "adapter",
                CurrentStationId: "CHARGE_01",
                CurrentTaskId: null,
                AgvId: "AGV-01")
        ];

        var status = Assert.Single(await service.GetFleetStatusAsync(CancellationToken.None));

        Assert.Null(status.ActiveTask);
    }

    [Fact]
    public async Task Fleet_status_does_not_fall_back_to_a_stale_task_when_snapshot_operation_is_unknown()
    {
        var adapter = new FleetStatusAdapter();
        var service = CreateService(adapter);

        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);

        adapter.Snapshots =
        [
            new AgvSnapshotResponse(
                Online: true,
                ControlOwner: "adapter",
                CurrentStationId: "CHARGE_01",
                CurrentTaskId: Guid.NewGuid(),
                AgvId: "AGV-01")
        ];

        var status = Assert.Single(await service.GetFleetStatusAsync(CancellationToken.None));

        Assert.Null(status.ActiveTask);
    }

    [Fact]
    public async Task Fleet_status_correlates_each_active_task_to_its_own_agv()
    {
        var adapter = new FleetStatusAdapter();
        var service = CreateService(adapter);

        var first = await service.CreateAsync(new(2, 4), CancellationToken.None);
        adapter.AgvByOperation[TransportOperationIds.Pickup(first.Id)] = "AGV-01";
        var firstDispatched = await service.DispatchAsync(first.Id, CancellationToken.None);
        var second = await service.CreateAsync(new(2, 4), CancellationToken.None);
        adapter.AgvByOperation[TransportOperationIds.Pickup(second.Id)] = "AGV-02";
        var secondDispatched = await service.DispatchAsync(second.Id, CancellationToken.None);

        adapter.Snapshots =
        [
            new AgvSnapshotResponse(
                Online: true,
                ControlOwner: "adapter",
                CurrentStationId: "CHARGE_01",
                CurrentTaskId: TransportOperationIds.Pickup(first.Id),
                AgvId: "AGV-01"),
            new AgvSnapshotResponse(
                Online: true,
                ControlOwner: "adapter",
                CurrentStationId: "CHARGE_01",
                CurrentTaskId: TransportOperationIds.Pickup(second.Id),
                AgvId: "AGV-02")
        ];

        var statuses = await service.GetFleetStatusAsync(CancellationToken.None);

        Assert.Equal(2, statuses.Count);
        var firstStatus = Assert.Single(statuses, status => status.Snapshot.AgvId == "AGV-01");
        var secondStatus = Assert.Single(statuses, status => status.Snapshot.AgvId == "AGV-02");
        Assert.Equal(first.Id, firstStatus.ActiveTask?.TransportTaskId);
        Assert.Equal(second.Id, secondStatus.ActiveTask?.TransportTaskId);
        Assert.Equal("MovingToPickup", firstStatus.ActiveTask?.MesStatus);
        Assert.Equal("MovingToPickup", secondStatus.ActiveTask?.MesStatus);
        Assert.NotEqual(firstStatus.ActiveTask?.TransportTaskId, secondStatus.ActiveTask?.TransportTaskId);
        Assert.Equal("AGV-01", firstDispatched.ActiveAgvId);
        Assert.Equal("AGV-02", secondDispatched.ActiveAgvId);
    }

    private static TaskService CreateService(FleetStatusAdapter adapter)
    {
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TaskService(new TaskRepository(new MesDbContext(options)), adapter);
    }

    private sealed class FleetStatusAdapter : IAgvGateway, IFleetAwareAgvGateway
    {
        public IReadOnlyList<AgvSnapshotResponse> Snapshots { get; set; } =
        [
            new AgvSnapshotResponse(true, "adapter", null, null, "AGV-01")
        ];

        public Dictionary<Guid, string> AgvByOperation { get; } = [];

        public Task<AgvTaskResponse> DispatchAsync(
            Guid operationId,
            string targetStationId,
            CancellationToken cancellationToken)
        {
            var agvId = AgvByOperation.GetValueOrDefault(operationId, "AGV-01");
            return Task.FromResult(new AgvTaskResponse(
                operationId,
                operationId.ToString("N"),
                targetStationId,
                "moving",
                null,
                agvId));
        }

        public Task<AgvTaskResponse?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult<AgvTaskResponse?>(new AgvTaskResponse(
                operationId,
                operationId.ToString("N"),
                "SAMPLE_01",
                "moving",
                null,
                AgvByOperation.GetValueOrDefault(operationId, "AGV-01")));

        public Task<AgvTaskResponse?> CancelAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult<AgvTaskResponse?>(null);

        public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Snapshots[0] with { CurrentTaskId = null });

        public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(
            string agvId,
            string command,
            Guid? taskId,
            CancellationToken cancellationToken) =>
            Task.FromResult<AgvTaskResponse?>(null);

        public Task<IReadOnlyList<AgvSnapshotResponse>> GetFleetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Snapshots);
    }
}
