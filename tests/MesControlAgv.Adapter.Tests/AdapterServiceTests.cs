using MesControlAgv.Adapter.Contracts;
using MesControlAgv.Adapter.Data;
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

    private static AdapterService CreateService(FakeSimulatorClient simulator)
    {
        var options = new DbContextOptionsBuilder<AdapterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AdapterService(new AdapterDbContext(options), simulator);
    }
}

internal sealed class FakeSimulatorClient : ISimulatorClient
{
    public int NavigateCalls { get; private set; }
    public int StatusCalls { get; private set; }
    public bool ThrowTimeout { get; init; }
    public AgvSnapshotResponse Snapshot { get; init; } = new(true, "adapter", "CHARGE_01", null);
    public AdapterTaskResponse? ReconciledTask { get; init; }

    public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);

    public Task<AdapterTaskResponse?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        StatusCalls++;
        return Task.FromResult(ReconciledTask);
    }

    public Task<AdapterTaskResponse> NavigateAsync(Guid taskId, string stationId, CancellationToken cancellationToken)
    {
        NavigateCalls++;
        if (ThrowTimeout) throw new TimeoutException();
        return Task.FromResult(new AdapterTaskResponse(taskId, $"device-{taskId:N}", stationId, "moving", null));
    }
}
