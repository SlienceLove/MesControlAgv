using MesControlAgv.Adapter.Drivers;
using MesControlAgv.Adapter.Services;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Tests;

public abstract class AgvDriverContractTests
{
    protected const string AgvId = "AGV-01";

    protected abstract IAgvDriver CreateDriver(RecordingDeviceClient device);

    [Fact]
    public async Task Connect_delegates_to_the_device_control_handshake()
    {
        var device = new RecordingDeviceClient();

        await CreateDriver(device).ConnectAsync(CancellationToken.None);

        Assert.Equal(1, device.EnsureControlCalls);
    }

    [Fact]
    public async Task Snapshot_is_scoped_to_the_requested_agv()
    {
        var device = new RecordingDeviceClient
        {
            Snapshot = new AgvSnapshotResponse(true, "adapter", "CHARGE_01", null, "device-default")
        };

        var snapshot = await CreateDriver(device).GetSnapshotAsync(AgvId, CancellationToken.None);

        Assert.Equal(1, device.SnapshotCalls);
        Assert.Equal(AgvId, snapshot.AgvId);
        Assert.Equal("CHARGE_01", snapshot.CurrentStationId);
    }

    [Fact]
    public async Task Dispatch_preserves_the_normalized_command()
    {
        var device = new RecordingDeviceClient();
        var taskId = Guid.NewGuid();
        var command = new AgvDispatchCommand(taskId, AgvId, "DROP_01", "PICK_01", ["PICK_01", "DROP_01"]);

        var result = await CreateDriver(device).DispatchAsync(command, CancellationToken.None);

        Assert.Equal(taskId, device.NavigateCommand?.TaskId);
        Assert.Equal("PICK_01", device.NavigateCommand?.SourceStationId);
        Assert.Equal("DROP_01", device.NavigateCommand?.TargetStationId);
        Assert.Equal(command.Path, device.NavigateCommand?.Path);
        Assert.Equal(taskId, result.TaskId);
        Assert.Equal("DROP_01", result.TargetStationId);
    }

    [Fact]
    public async Task Task_controls_delegate_the_task_identifier()
    {
        var device = new RecordingDeviceClient();
        var taskId = Guid.NewGuid();
        var command = new AgvControlCommand(taskId, AgvId);
        var driver = CreateDriver(device);

        await driver.PauseAsync(command, CancellationToken.None);
        await driver.ResumeAsync(command, CancellationToken.None);
        await driver.CancelAsync(command, CancellationToken.None);

        Assert.Equal([taskId], device.PauseTaskIds);
        Assert.Equal([taskId], device.ResumeTaskIds);
        Assert.Equal([taskId], device.CancelTaskIds);
    }

    [Fact]
    public async Task Operations_reject_an_agv_other_than_the_configured_instance()
    {
        var device = new RecordingDeviceClient();
        var driver = CreateDriver(device);
        var taskId = Guid.NewGuid();
        const string otherAgvId = "AGV-02";

        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.GetSnapshotAsync(otherAgvId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.DispatchAsync(new AgvDispatchCommand(taskId, otherAgvId, "DROP_01"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.PauseAsync(new AgvControlCommand(taskId, otherAgvId), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ResumeAsync(new AgvControlCommand(taskId, otherAgvId), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.CancelAsync(new AgvControlCommand(taskId, otherAgvId), CancellationToken.None));

        Assert.Equal(0, device.TotalCalls);
    }

    [Fact]
    public void Capabilities_match_the_standard_normalized_driver_contract()
    {
        var capabilities = CreateDriver(new RecordingDeviceClient()).Capabilities;

        Assert.Equal(AgvCapabilitiesResponse.Standard, capabilities);
    }
}

public sealed class SimulatorDriverContractTests : AgvDriverContractTests
{
    protected override IAgvDriver CreateDriver(RecordingDeviceClient device) =>
        new SimulatorDriver(device, options: new AgvDriverOptions(AgvId));
}

public sealed class VendorTcpDriverContractTests : AgvDriverContractTests
{
    protected override IAgvDriver CreateDriver(RecordingDeviceClient device) =>
        new VendorTcpDriver(device, new AgvDriverOptions(AgvId));

    [Theory]
    [InlineData("api")]
    [InlineData("protocol")]
    public async Task Vendor_protocol_failures_are_normalized(string failure)
    {
        var device = new RecordingDeviceClient
        {
            NavigateException = failure == "api"
                ? new AgvApiException(3066, 40020, "control unavailable")
                : new AgvProtocolException("invalid response packet")
        };
        var driver = CreateDriver(device);

        var exception = await Assert.ThrowsAsync<AgvDriverException>(() => driver.DispatchAsync(
            new AgvDispatchCommand(Guid.NewGuid(), AgvId, "DROP_01"),
            CancellationToken.None));

        Assert.Equal(VendorTcpDriver.DriverKind, exception.DriverId);
        Assert.Equal("dispatch", exception.Operation);
        Assert.Same(device.NavigateException, exception.InnerException);
    }
}

public sealed class RecordingDeviceClient : IAgvDeviceClient
{
    public int EnsureControlCalls { get; private set; }
    public int SnapshotCalls { get; private set; }
    public List<Guid> PauseTaskIds { get; } = [];
    public List<Guid> ResumeTaskIds { get; } = [];
    public List<Guid> CancelTaskIds { get; } = [];
    public (Guid TaskId, string? SourceStationId, string TargetStationId, IReadOnlyList<string>? Path)? NavigateCommand { get; private set; }
    public Exception? NavigateException { get; init; }
    public AgvSnapshotResponse Snapshot { get; init; } = new(true, "adapter", "CHARGE_01", null);

    public int TotalCalls => EnsureControlCalls + SnapshotCalls + PauseTaskIds.Count + ResumeTaskIds.Count + CancelTaskIds.Count + (NavigateCommand is null ? 0 : 1);

    public Task EnsureControlAsync(CancellationToken cancellationToken)
    {
        EnsureControlCalls++;
        return Task.CompletedTask;
    }

    public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        SnapshotCalls++;
        return Task.FromResult(Snapshot);
    }

    public Task<AgvTaskResponse?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult<AgvTaskResponse?>(null);

    public Task<AgvTaskResponse> NavigateAsync(
        Guid taskId,
        string? sourceStationId,
        string stationId,
        CancellationToken cancellationToken) =>
        NavigateAsync(taskId, sourceStationId, stationId, null, cancellationToken);

    public Task<AgvTaskResponse> NavigateAsync(
        Guid taskId,
        string? sourceStationId,
        string stationId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken)
    {
        NavigateCommand = (taskId, sourceStationId, stationId, path);
        return NavigateException is null
            ? Task.FromResult(new AgvTaskResponse(taskId, $"device-{taskId:N}", stationId, "moving", null))
            : Task.FromException<AgvTaskResponse>(NavigateException);
    }

    public Task<AgvTaskResponse?> PauseAsync(Guid taskId, CancellationToken cancellationToken)
    {
        PauseTaskIds.Add(taskId);
        return Task.FromResult<AgvTaskResponse?>(TaskResult(taskId, "paused"));
    }

    public Task<AgvTaskResponse?> ResumeAsync(Guid taskId, CancellationToken cancellationToken)
    {
        ResumeTaskIds.Add(taskId);
        return Task.FromResult<AgvTaskResponse?>(TaskResult(taskId, "moving"));
    }

    public Task<AgvTaskResponse?> CancelAsync(Guid taskId, CancellationToken cancellationToken)
    {
        CancelTaskIds.Add(taskId);
        return Task.FromResult<AgvTaskResponse?>(TaskResult(taskId, "cancelled"));
    }

    private static AgvTaskResponse TaskResult(Guid taskId, string state) =>
        new(taskId, $"device-{taskId:N}", "DROP_01", state, null);
}
