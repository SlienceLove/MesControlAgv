using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Tests;

public sealed class DriverRegistryTests
{
    [Fact]
    public void Registry_lookup_is_case_insensitive_and_rejects_duplicates()
    {
        var registry = new DriverRegistry([new FakeDriverFactory("simulator")]);

        Assert.True(registry.Contains("SIMULATOR"));
        Assert.Equal(["simulator"], registry.DriverIds);
        Assert.Throws<InvalidOperationException>(() => registry.Register(new FakeDriverFactory("SIMULATOR")));
    }

    [Fact]
    public void Registry_creates_driver_with_options()
    {
        var factory = new FakeDriverFactory("vendor");
        var registry = new DriverRegistry([factory]);

        var driver = registry.Create("vendor", new AgvDriverOptions("AGV-07"));

        Assert.Equal("vendor", driver.DriverId);
        Assert.Equal("AGV-07", factory.LastOptions?.DefaultAgvId);
    }

    private sealed class FakeDriverFactory(string driverId) : IAgvDriverFactory
    {
        public string DriverId { get; } = driverId;
        public AgvDriverOptions? LastOptions { get; private set; }

        public IAgvDriver Create(AgvDriverOptions options)
        {
            LastOptions = options;
            return new FakeDriver(DriverId);
        }
    }

    private sealed class FakeDriver(string driverId) : IAgvDriver
    {
        public string DriverId { get; } = driverId;
        public AgvCapabilitiesResponse Capabilities => AgvCapabilitiesResponse.Standard;
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<AgvSnapshotResponse> GetSnapshotAsync(string agvId, CancellationToken cancellationToken) =>
            Task.FromResult(new AgvSnapshotResponse(true, "driver", null, null, agvId));
        public Task<AgvTaskResponse> DispatchAsync(AgvDispatchCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(new AgvTaskResponse(command.TaskId, DriverId, command.TargetStationId, "moving", null, command.AgvId));
        public Task<AgvTaskResponse?> PauseAsync(AgvControlCommand command, CancellationToken cancellationToken) => Task.FromResult<AgvTaskResponse?>(null);
        public Task<AgvTaskResponse?> ResumeAsync(AgvControlCommand command, CancellationToken cancellationToken) => Task.FromResult<AgvTaskResponse?>(null);
        public Task<AgvTaskResponse?> CancelAsync(AgvControlCommand command, CancellationToken cancellationToken) => Task.FromResult<AgvTaskResponse?>(null);
    }
}

