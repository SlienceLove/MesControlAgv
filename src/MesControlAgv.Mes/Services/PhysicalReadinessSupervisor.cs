using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Permanent read-only physical-device observer.  It performs an immediate
/// startup observation, invalidates trusted state on disconnect, and requires
/// a new stable full preflight after reconnect.  It never invokes a mutating
/// gateway method and never replays an ambiguous operation.
/// </summary>
public sealed class PhysicalReadinessSupervisor : BackgroundService, IPhysicalReadinessSupervisor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProfileConfiguration _profile;
    private readonly PhysicalReadinessSupervisorOptions _options;
    private readonly PhysicalReadinessStateStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PhysicalReadinessSupervisor> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly IReadOnlyList<PhysicalDeviceDescriptor> _descriptors;
    private readonly bool _enabled;

    public PhysicalReadinessSupervisor(
        IServiceScopeFactory scopeFactory,
        ProfileConfiguration profile,
        PhysicalReadinessSupervisorOptions options,
        PhysicalReadinessStateStore store,
        TimeProvider timeProvider,
        ILogger<PhysicalReadinessSupervisor> logger)
    {
        _scopeFactory = scopeFactory;
        _profile = profile;
        _options = options;
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
        _descriptors = BuildDescriptors(profile);
        _enabled = options.Enabled && !profile.Features.UseSimulator;
        _store.Configure(
            _enabled,
            _descriptors,
            _timeProvider.GetUtcNow(),
            options.Enabled && profile.Features.UseSimulator
                ? PhysicalReadinessReasonCodes.SimulatorProfile
                : PhysicalReadinessReasonCodes.SupervisorDisabled,
            options.ObservationStaleAfter);
    }

    public bool Enabled => _enabled;

    public PhysicalReadinessResponse GetSnapshot() => _store.GetSnapshot();

    public bool TryGetDevice(
        string deviceId,
        out PhysicalDeviceReadinessSnapshot snapshot) =>
        _store.TryGetDevice(deviceId, out snapshot);

    public bool IsCurrentAndReady(
        string deviceId,
        long? expectedEpoch,
        out string? reason) =>
        _store.IsCurrentAndReady(deviceId, expectedEpoch, out reason);

    public bool IsCurrentAndReady(
        string deviceId,
        long? expectedEpoch,
        string? expectedSupervisorInstanceId,
        out string? reason) =>
        _store.IsCurrentAndReady(
            deviceId,
            expectedEpoch,
            expectedSupervisorInstanceId,
            out reason);

    public bool AcknowledgeAuthorization(string deviceId, long expectedEpoch) =>
        _store.AcknowledgeAuthorization(deviceId, expectedEpoch);

    public async Task<PhysicalReadinessResponse> RefreshAsync(
        bool forceFull,
        CancellationToken cancellationToken)
    {
        if (!_enabled) return _store.GetSnapshot();

        await _refreshGate.WaitAsync(cancellationToken);
        _store.SetRefreshInProgress(true);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var probes = scope.ServiceProvider
                .GetServices<IPhysicalDeviceReadinessProbe>()
                .ToArray();

            foreach (var descriptor in _descriptors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPreflight = forceFull || _store.ShouldRunFullPreflight(
                    descriptor.DeviceId,
                    _timeProvider.GetUtcNow(),
                    _options.FullPreflightInterval);
                var probe = probes.FirstOrDefault(candidate => candidate.CanProbe(descriptor));
                if (probe is null)
                {
                    _store.Apply(
                        descriptor,
                        MissingProbeObservation(descriptor),
                        _options.ReadyStabilityWindow,
                        _options.RequireFullPreflightForReady,
                        _timeProvider.GetUtcNow());
                    continue;
                }

                try
                {
                    var observation = await probe.ProbeAsync(
                        descriptor,
                        fullPreflight,
                        cancellationToken);
                    _store.Apply(
                        descriptor,
                        observation,
                        _options.ReadyStabilityWindow,
                        _options.RequireFullPreflightForReady,
                        _timeProvider.GetUtcNow());
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Read-only readiness probe for {DeviceFamily}/{DeviceId} failed; the prior Ready state is invalidated.",
                        descriptor.DeviceFamily,
                        descriptor.DeviceId);
                    _store.Apply(
                        descriptor,
                        FailedObservation(descriptor, exception),
                        _options.ReadyStabilityWindow,
                        _options.RequireFullPreflightForReady,
                        _timeProvider.GetUtcNow());
                }
            }

            return _store.GetSnapshot();
        }
        finally
        {
            _store.CompleteRefresh(_timeProvider.GetUtcNow());
            _refreshGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;

        try
        {
            await RefreshAsync(forceFull: true, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "The startup physical-readiness cycle failed closed; no device command was issued.");
        }

        var interval = _options.PollInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(2)
            : _options.PollInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken);
                await RefreshAsync(forceFull: false, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "One physical-readiness polling cycle failed closed; no device command was issued.");
            }
        }
    }

    private PhysicalDeviceReadinessObservation MissingProbeObservation(
        PhysicalDeviceDescriptor descriptor) => new()
        {
            DeviceId = descriptor.DeviceId,
            DeviceFamily = descriptor.DeviceFamily,
            ProbeSucceeded = false,
            Online = null,
            IsFullPreflight = false,
            BlockingReasons = [PhysicalReadinessReasonCodes.ProbeNotRegistered],
            ObservedAtUtc = _timeProvider.GetUtcNow(),
            Error = $"No read-only readiness probe is registered for device family '{descriptor.DeviceFamily}'."
        };

    private PhysicalDeviceReadinessObservation FailedObservation(
        PhysicalDeviceDescriptor descriptor,
        Exception exception) => new()
        {
            DeviceId = descriptor.DeviceId,
            DeviceFamily = descriptor.DeviceFamily,
            ProbeSucceeded = false,
            Online = null,
            IsFullPreflight = false,
            BlockingReasons = [PhysicalReadinessReasonCodes.ProbeUnavailable],
            ObservedAtUtc = _timeProvider.GetUtcNow(),
            Error = exception.Message
        };

    private static IReadOnlyList<PhysicalDeviceDescriptor> BuildDescriptors(
        ProfileConfiguration profile)
    {
        var devices = new List<PhysicalDeviceDescriptor>();
        devices.AddRange(profile.Agvs
            .Where(agv => agv.Enabled)
            .Select(agv => new PhysicalDeviceDescriptor(
                agv.AgvId,
                WorkflowDeviceFamilyIds.Agv,
                RequiredForScheduling: !profile.Features.UseSimulator,
                Enabled: true,
                ControlEnabled: !profile.Features.UseSimulator)));
        devices.AddRange(profile.WorkflowDevices
            .Where(device => device.Enabled)
            .Select(device => new PhysicalDeviceDescriptor(
                device.DeviceId,
                device.DeviceFamily,
                RequiredForScheduling: device.ControlEnabled,
                Enabled: true,
                ControlEnabled: device.ControlEnabled)));
        return devices
            .Where(device => !string.IsNullOrWhiteSpace(device.DeviceId) &&
                             !string.IsNullOrWhiteSpace(device.DeviceFamily))
            .GroupBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }
}
