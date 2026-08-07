using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Adapter.Services;

namespace MesControlAgv.Adapter.Drivers;

/// <summary>
/// Adapts the existing simulator HTTP client(s) to the normalized driver boundary.
/// The existing clients remain untouched and can continue serving the current MVP APIs.
/// </summary>
public sealed class SimulatorDriver : IAgvDriver
{
    public const string DriverKind = "simulator";

    private readonly IAgvDeviceClient _device;
    private readonly IAgvFleetDeviceClient? _fleet;
    private readonly AgvDriverOptions _options;

    public SimulatorDriver(
        IAgvDeviceClient device,
        IAgvFleetDeviceClient? fleet = null,
        AgvDriverOptions? options = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _fleet = fleet;
        _options = options ?? new AgvDriverOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.DefaultAgvId);
    }

    public string DriverId => DriverKind;

    public AgvCapabilitiesResponse Capabilities => AgvCapabilitiesResponse.Standard;

    public Task ConnectAsync(CancellationToken cancellationToken) =>
        _device.EnsureControlAsync(cancellationToken);

    public async Task<AgvSnapshotResponse> GetSnapshotAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        EnsureAgvId(agvId);
        if (_fleet is null)
        {
            return (await _device.GetSnapshotAsync(cancellationToken)) with { AgvId = agvId };
        }

        var snapshot = (await _fleet.GetFleetSnapshotAsync(cancellationToken))
            .SingleOrDefault(item => StringComparer.Ordinal.Equals(item.AgvId, agvId));
        return snapshot ?? throw new KeyNotFoundException($"AGV {agvId} was not found in the simulator fleet.");
    }

    public Task<AgvTaskResponse> DispatchAsync(
        AgvDispatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureAgvId(command.AgvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TargetStationId);

        return _fleet is null
            ? _device.NavigateAsync(command.TaskId, command.SourceStationId, command.TargetStationId, command.Path, cancellationToken)
            : _fleet.NavigateAsync(
                command.AgvId,
                command.TaskId,
                command.SourceStationId,
                command.TargetStationId,
                command.Path,
                cancellationToken);
    }

    public Task<AgvTaskResponse?> PauseAsync(
        AgvControlCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureAgvId(command.AgvId);
        return _fleet is null
            ? _device.PauseAsync(command.TaskId, cancellationToken)
            : _fleet.PauseAsync(command.AgvId, command.TaskId, cancellationToken);
    }

    public Task<AgvTaskResponse?> ResumeAsync(
        AgvControlCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureAgvId(command.AgvId);
        return _fleet is null
            ? _device.ResumeAsync(command.TaskId, cancellationToken)
            : _fleet.ResumeAsync(command.AgvId, command.TaskId, cancellationToken);
    }

    public Task<AgvTaskResponse?> CancelAsync(
        AgvControlCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureAgvId(command.AgvId);
        return _fleet is null
            ? _device.CancelAsync(command.TaskId, cancellationToken)
            : _fleet.CancelAsync(command.AgvId, command.TaskId, cancellationToken);
    }

    private void EnsureAgvId(string agvId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        if (_fleet is null && !StringComparer.Ordinal.Equals(agvId, _options.DefaultAgvId))
        {
            throw new InvalidOperationException(
                $"The simulator client is configured for AGV '{_options.DefaultAgvId}', not '{agvId}'.");
        }
    }
}

public sealed class SimulatorDriverFactory : IAgvDriverFactory
{
    private readonly IAgvDeviceClient _device;
    private readonly IAgvFleetDeviceClient? _fleet;

    public SimulatorDriverFactory(IAgvDeviceClient device, IAgvFleetDeviceClient? fleet = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _fleet = fleet;
    }

    public string DriverId => SimulatorDriver.DriverKind;

    public IAgvDriver Create(AgvDriverOptions options) =>
        new SimulatorDriver(_device, _fleet, options);
}
