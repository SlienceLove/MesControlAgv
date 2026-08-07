using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Adapter.Services;

namespace MesControlAgv.Adapter.Drivers;

/// <summary>
/// Minimal normalized adapter over the existing TcpAgvClient contract.
/// It intentionally delegates protocol behavior to TcpAgvClient rather than replacing or
/// modifying that client; future vendor-specific extensions can be added behind this boundary.
/// </summary>
public sealed class VendorTcpDriver : IAgvDriver
{
    public const string DriverKind = "vendor-tcp";

    private readonly IAgvDeviceClient _device;
    private readonly AgvDriverOptions _options;

    public VendorTcpDriver(IAgvDeviceClient device, AgvDriverOptions? options = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _options = options ?? new AgvDriverOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.DefaultAgvId);
    }

    public string DriverId => DriverKind;

    public AgvCapabilitiesResponse Capabilities => new(
        SupportsPause: true,
        SupportsResume: true,
        SupportsCancel: true,
        SupportsEmergencyStop: false,
        SupportsLift: false,
        SupportsBarcode: false,
        SupportsStationConfirmation: true);

    public Task ConnectAsync(CancellationToken cancellationToken) =>
        RunVendorOperationAsync("connect", () => _device.EnsureControlAsync(cancellationToken));

    public async Task<AgvSnapshotResponse> GetSnapshotAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        EnsureAgvId(agvId);
        return (await RunVendorOperationAsync("snapshot", () => _device.GetSnapshotAsync(cancellationToken)))
            with { AgvId = agvId };
    }

    public Task<AgvTaskResponse> DispatchAsync(
        AgvDispatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureAgvId(command.AgvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TargetStationId);
        return RunVendorOperationAsync(
            "dispatch",
            () => _device.NavigateAsync(
                command.TaskId,
                command.SourceStationId,
                command.TargetStationId,
                command.Path,
                cancellationToken));
    }

    public Task<AgvTaskResponse?> PauseAsync(
        AgvControlCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureAgvId(command.AgvId);
        return RunVendorOperationAsync("pause", () => _device.PauseAsync(command.TaskId, cancellationToken));
    }

    public Task<AgvTaskResponse?> ResumeAsync(
        AgvControlCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureAgvId(command.AgvId);
        return RunVendorOperationAsync("resume", () => _device.ResumeAsync(command.TaskId, cancellationToken));
    }

    public Task<AgvTaskResponse?> CancelAsync(
        AgvControlCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureAgvId(command.AgvId);
        return RunVendorOperationAsync("cancel", () => _device.CancelAsync(command.TaskId, cancellationToken));
    }

    private void EnsureAgvId(string agvId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        if (!StringComparer.Ordinal.Equals(agvId, _options.DefaultAgvId))
        {
            throw new InvalidOperationException(
                $"VendorTcpDriver is configured for AGV '{_options.DefaultAgvId}', not '{agvId}'.");
        }
    }

    private async Task RunVendorOperationAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgvDriverException)
        {
            throw;
        }
        catch (Exception exception) when (exception is AgvApiException or AgvProtocolException)
        {
            throw new AgvDriverException(
                DriverId,
                operation,
                $"Vendor TCP operation '{operation}' failed.",
                exception);
        }
    }

    private async Task<T> RunVendorOperationAsync<T>(string operation, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgvDriverException)
        {
            throw;
        }
        catch (Exception exception) when (exception is AgvApiException or AgvProtocolException)
        {
            throw new AgvDriverException(
                DriverId,
                operation,
                $"Vendor TCP operation '{operation}' failed.",
                exception);
        }
    }
}

public sealed class VendorTcpDriverFactory : IAgvDriverFactory
{
    private readonly IAgvDeviceClient _device;

    public VendorTcpDriverFactory(IAgvDeviceClient device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public string DriverId => VendorTcpDriver.DriverKind;

    public IAgvDriver Create(AgvDriverOptions options) =>
        new VendorTcpDriver(_device, options);
}
