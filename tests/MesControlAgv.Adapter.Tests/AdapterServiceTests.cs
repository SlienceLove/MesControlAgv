using MesControlAgv.Adapter.Contracts;
using MesControlAgv.Adapter.Data;
using MesControlAgv.Adapter.Entities;
using MesControlAgv.Adapter.Services;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Adapter.Tests;

public class AdapterServiceTests
{
    [Fact]
    public async Task Duplicate_dispatch_does_not_send_a_second_navigation()
    {
        var simulator = new FakeSimulatorClient();
        var service = CreateService(simulator);
        var taskId = Guid.NewGuid();

        var first = await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);
        var second = await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);

        Assert.Equal(first.DeviceTaskId, second.DeviceTaskId);
        Assert.Equal(1, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Route_aware_dispatch_forwards_the_source_station()
    {
        var simulator = new FakeSimulatorClient();
        var service = CreateService(simulator);
        var taskId = Guid.NewGuid();

        await service.DispatchAsync(taskId, "SAMPLE_01", "ST_PREP_01", CancellationToken.None);

        Assert.Equal("SAMPLE_01", simulator.SourceStationId);
    }

    [Fact]
    public async Task Duplicate_dispatch_checks_control_owner_before_returning_persisted_operation()
    {
        var simulator = new FakeSimulatorClient();
        var service = CreateService(simulator);
        var taskId = Guid.NewGuid();

        await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);
        simulator.Snapshot = new(false, "roboshop", null, null);

        await Assert.ThrowsAsync<ControlUnavailableException>(() => service.DispatchAsync(
            taskId, "SAMPLE_01", CancellationToken.None));

        Assert.Equal(1, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Failed_persisted_dispatch_retries_navigation_with_same_task_id()
    {
        var taskId = Guid.NewGuid();
        var simulator = new FakeSimulatorClient();
        var (service, database) = CreateServiceWithDatabase(simulator);
        database.Tasks.Add(new AdapterTask
        {
            TaskId = taskId,
            DeviceTaskId = "device-failed",
            TargetStationId = "SAMPLE_01",
            State = "failed",
            LastError = "device unavailable"
        });
        await database.SaveChangesAsync();

        var result = await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);
        var persisted = await database.Tasks.FindAsync([taskId]);

        Assert.Equal("moving", result.State);
        Assert.Equal(1, simulator.NavigateCalls);
        Assert.NotNull(persisted);
        Assert.Equal("moving", persisted.State);
    }

    [Fact]
    public async Task Concurrent_dispatches_do_not_retry_a_failed_in_flight_operation()
    {
        var taskId = Guid.NewGuid();
        var navigationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowNavigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var simulator = new FakeSimulatorClient
        {
            ReturnFailed = true,
            NavigationStarted = navigationStarted,
            AllowNavigation = allowNavigation
        };
        var service = CreateService(simulator);

        var first = service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);
        await navigationStarted.Task;
        var second = service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);
        Assert.False(second.IsCompleted);

        allowNavigation.TrySetResult(true);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal("failed", result.State));
        Assert.Equal(1, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Non_adapter_control_owner_blocks_dispatch()
    {
        var simulator = new FakeSimulatorClient { Snapshot = new(false, "roboshop", null, null) };
        var service = CreateService(simulator);

        await Assert.ThrowsAsync<ControlUnavailableException>(() => service.DispatchAsync(
            Guid.NewGuid(), "SAMPLE_01", CancellationToken.None));
    }

    [Fact]
    public async Task Timeout_reconciles_to_actual_device_state_before_unknown()
    {
        var taskId = Guid.NewGuid();
        var simulator = new FakeSimulatorClient
        {
            ThrowTimeout = true,
            ReconciledTask = new(taskId, "device-1", "SAMPLE_01", "moving", null)
        };
        var service = CreateService(simulator);

        var result = await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);

        Assert.Equal("moving", result.State);
        Assert.Equal(1, simulator.StatusCalls);
    }

    [Fact]
    public async Task Get_task_refreshes_persisted_state_from_device()
    {
        var taskId = Guid.NewGuid();
        var simulator = new FakeSimulatorClient
        {
            ReconciledTask = new AdapterTaskResponse(taskId, "device-1", "SAMPLE_01", "arrived", null)
        };
        var service = CreateService(simulator);
        await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);

        var task = await service.GetAsync(taskId, CancellationToken.None);

        Assert.NotNull(task);
        Assert.Equal("arrived", task.State);
        Assert.Equal(1, simulator.StatusCalls);
    }

    [Fact]
    public async Task Cancel_persists_only_after_device_confirms_cancellation()
    {
        var taskId = Guid.NewGuid();
        var simulator = new FakeSimulatorClient { CancelState = "moving" };
        var (service, database) = CreateServiceWithDatabase(simulator);
        await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(taskId, CancellationToken.None));

        var persisted = await database.Tasks.FindAsync([taskId]);
        Assert.NotNull(persisted);
        Assert.Equal("moving", persisted.State);
        Assert.Equal(1, simulator.CancelCalls);
    }

    [Fact]
    public async Task Cancel_persists_confirmed_device_cancellation()
    {
        var taskId = Guid.NewGuid();
        var simulator = new FakeSimulatorClient();
        var (service, database) = CreateServiceWithDatabase(simulator);
        await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);

        var result = await service.CancelAsync(taskId, CancellationToken.None);

        Assert.Equal("cancelled", result!.State);
        Assert.Equal("cancelled", (await database.Tasks.FindAsync([taskId]))!.State);
    }

    [Fact]
    public async Task Fleet_dispatch_assigns_idle_agvs_and_preserves_assignment_on_duplicate()
    {
        var fleet = new FakeFleetClient();
        var device = new FakeSimulatorClient();
        var (service, _) = CreateFleetService(device, fleet);
        var firstTaskId = Guid.NewGuid();
        var secondTaskId = Guid.NewGuid();

        var first = await service.DispatchAsync(firstTaskId, "SAMPLE_01", "ST_PREP_01", CancellationToken.None);
        var second = await service.DispatchAsync(secondTaskId, "SAMPLE_01", "ST_PREP_01", CancellationToken.None);
        var duplicate = await service.DispatchAsync(firstTaskId, "SAMPLE_01", "ST_PREP_01", CancellationToken.None);

        Assert.Equal("AGV-01", first.AgvId);
        Assert.Equal("AGV-02", second.AgvId);
        Assert.Equal(first.AgvId, duplicate.AgvId);
        Assert.NotEmpty(first.Path!);
        Assert.Equal(2, fleet.NavigateCalls);
    }

    private static AdapterService CreateService(FakeSimulatorClient simulator)
    {
        return CreateServiceWithDatabase(simulator).Service;
    }

    private static (AdapterService Service, AdapterDbContext Database) CreateServiceWithDatabase(FakeSimulatorClient simulator)
    {
        var options = new DbContextOptionsBuilder<AdapterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var database = new AdapterDbContext(options);
        return (new AdapterService(database, simulator), database);
    }

    private static (AdapterService Service, AdapterDbContext Database) CreateFleetService(
        FakeSimulatorClient simulator,
        FakeFleetClient fleet)
    {
        var options = new DbContextOptionsBuilder<AdapterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var database = new AdapterDbContext(options);
        var scheduler = new MesControlAgv.Domain.MultiAgvScheduler(
            new MesControlAgv.Domain.PathPlanner(MesControlAgv.Domain.AgvMap.Default));
        return (new AdapterService(database, simulator, fleet, scheduler), database);
    }
}

internal sealed class FakeSimulatorClient : ISimulatorClient
{
    private int _navigateCalls;
    private int _statusCalls;
    private int _cancelCalls;

    public int NavigateCalls => Volatile.Read(ref _navigateCalls);
    public int StatusCalls => Volatile.Read(ref _statusCalls);
    public int CancelCalls => Volatile.Read(ref _cancelCalls);
    public string? SourceStationId { get; private set; }
    public bool ThrowTimeout { get; init; }
    public bool ReturnFailed { get; init; }
    public string? CancelState { get; init; } = "cancelled";
    public string PauseState { get; init; } = "paused";
    public string ResumeState { get; init; } = "moving";
    public AgvSnapshotResponse Snapshot { get; set; } = new(true, "adapter", "CHARGE_01", null);
    public AdapterTaskResponse? ReconciledTask { get; init; }
    public TaskCompletionSource<bool>? NavigationStarted { get; init; }
    public TaskCompletionSource<bool>? AllowNavigation { get; init; }

    public Task EnsureControlAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);

    public Task<AdapterTaskResponse?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _statusCalls);
        return Task.FromResult(ReconciledTask);
    }

    public async Task<AdapterTaskResponse> NavigateAsync(Guid taskId, string? sourceStationId, string stationId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _navigateCalls);
        SourceStationId = sourceStationId;
        NavigationStarted?.TrySetResult(true);
        if (ThrowTimeout) throw new TimeoutException();
        if (AllowNavigation is not null) await AllowNavigation.Task.WaitAsync(cancellationToken);
        return new AdapterTaskResponse(taskId, $"device-{taskId:N}", stationId, ReturnFailed ? "failed" : "moving", ReturnFailed ? "device unavailable" : null);
    }

    public Task<AdapterTaskResponse?> PauseAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult<AdapterTaskResponse?>(new AdapterTaskResponse(taskId, $"device-{taskId:N}", "SAMPLE_01", PauseState, null));

    public Task<AdapterTaskResponse?> ResumeAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult<AdapterTaskResponse?>(new AdapterTaskResponse(taskId, $"device-{taskId:N}", "SAMPLE_01", ResumeState, null));

    public Task<AdapterTaskResponse?> CancelAsync(Guid taskId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _cancelCalls);
        return Task.FromResult(CancelState is null
            ? null
            : new AdapterTaskResponse(taskId, $"device-{taskId:N}", "SAMPLE_01", CancelState, null));
    }
}

internal sealed class FakeFleetClient : IAgvFleetDeviceClient
{
    private readonly Dictionary<string, AgvSnapshotResponse> _snapshots = new(StringComparer.Ordinal)
    {
        ["AGV-01"] = new(true, "adapter", "CHARGE_01", null, "AGV-01"),
        ["AGV-02"] = new(true, "adapter", "CHARGE_01", null, "AGV-02"),
        ["AGV-03"] = new(true, "adapter", "CHARGE_01", null, "AGV-03")
    };
    private readonly Dictionary<(string AgvId, Guid TaskId), AdapterTaskResponse> _tasks = [];

    public int NavigateCalls { get; private set; }

    public Task<IReadOnlyList<AgvSnapshotResponse>> GetFleetSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgvSnapshotResponse>>(_snapshots.Values.ToArray());

    public Task<AdapterTaskResponse?> GetTaskAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult(_tasks.GetValueOrDefault((agvId, taskId)));

    public Task<AdapterTaskResponse> NavigateAsync(
        string agvId,
        Guid taskId,
        string? sourceStationId,
        string stationId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken)
    {
        NavigateCalls++;
        var task = new AdapterTaskResponse(taskId, taskId.ToString("N"), stationId, "moving", null, agvId, path);
        _tasks[(agvId, taskId)] = task;
        _snapshots[agvId] = _snapshots[agvId] with { CurrentTaskId = taskId };
        return Task.FromResult(task);
    }

    public Task<AdapterTaskResponse?> PauseAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult<AdapterTaskResponse?>(_tasks.GetValueOrDefault((agvId, taskId)));

    public Task<AdapterTaskResponse?> ResumeAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult<AdapterTaskResponse?>(_tasks.GetValueOrDefault((agvId, taskId)));

    public Task<AdapterTaskResponse?> CancelAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult<AdapterTaskResponse?>(_tasks.GetValueOrDefault((agvId, taskId)) is { } task
            ? task with { State = "cancelled" }
            : null);
}
