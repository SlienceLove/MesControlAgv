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
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IReadOnlyDictionary<string, DeviceProbeSlot> _slots;
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
        if (options.ProbeTimeout <= TimeSpan.Zero || options.ProbeTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(options.ProbeTimeout));
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
        _descriptors = BuildDescriptors(profile);
        _slots = _descriptors.ToDictionary(d => d.DeviceId, _ => new DeviceProbeSlot(), StringComparer.OrdinalIgnoreCase);
        _enabled = options.Enabled && !profile.Features.UseSimulator;
        _store.Configure(
            _enabled,
            _descriptors,
            _timeProvider.GetUtcNow(),
            options.Enabled && profile.Features.UseSimulator
                ? PhysicalReadinessReasonCodes.SimulatorProfile
                : PhysicalReadinessReasonCodes.SupervisorDisabled,
            options.ObservationStaleAfter,
            BuildConfigurationFingerprint());
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

    public bool AcknowledgeAuthorization(
        string deviceId,
        long expectedEpoch,
        string? expectedSupervisorInstanceId) =>
        _store.AcknowledgeAuthorization(
            deviceId,
            expectedEpoch,
            expectedSupervisorInstanceId);


    public async Task<PhysicalReadinessResponse> RefreshAsync(
        bool forceFull,
        CancellationToken cancellationToken)
    {
        if (!_enabled) return _store.GetSnapshot();

        // Each result is published as it arrives. Background device loops keep running
        // while a manual all-device refresh waits for its own bounded observations.
        await Task.WhenAll(_descriptors.Select(d => RefreshDeviceAsync(d, forceFull, cancellationToken)));
        return _store.GetSnapshot();
    }

    private async Task RefreshDeviceAsync(PhysicalDeviceDescriptor descriptor, bool forceFull, CancellationToken cancellationToken)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var slot = _slots[descriptor.DeviceId];
        if (forceFull) Interlocked.Exchange(ref slot.FullRequested, 1);
        // Busy includes a timed-out probe that ignored cancellation. Never queue another
        // request for that device; other device loops have their own independent slots.
        if (!await slot.Gate.WaitAsync(0, caller.Token)) return;
        _store.BeginRefresh();
        IServiceScope? scope = null;
        CancellationTokenSource? request = null;
        Task<PhysicalDeviceReadinessObservation>? pending = null;
        try
        {
            scope = _scopeFactory.CreateScope();
            request = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
            var fullPreflight = Interlocked.Exchange(ref slot.FullRequested, 0) != 0 ||
                _store.ShouldRunFullPreflight(descriptor.DeviceId, _timeProvider.GetUtcNow(), _options.FullPreflightInterval);
            var probe = scope.ServiceProvider.GetServices<IPhysicalDeviceReadinessProbe>().FirstOrDefault(p => p.CanProbe(descriptor));
            if (probe is null)
            {
                Apply(descriptor, MissingProbeObservation(descriptor));
                return;
            }
            // Isolate synchronous work in a probe as well as its asynchronous I/O.
            pending = Task.Run(() => probe.ProbeAsync(descriptor, fullPreflight, request.Token), request.Token);
            var observation = await pending.WaitAsync(_options.ProbeTimeout, _timeProvider, request.Token);
            caller.Token.ThrowIfCancellationRequested();
            Apply(descriptor, observation);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            request?.Cancel();
            _logger.LogWarning(exception, "Read-only readiness probe for {DeviceId} failed; other devices continue polling.", descriptor.DeviceId);
            Apply(descriptor, FailedObservation(descriptor, exception));
        }
        finally
        {
            if (pending is not null)
            {
                // Retain the DI scope and device slot until the abandoned I/O finishes.
                // Its late result is NEVER applied to readiness, including after shutdown.
                // Observe completed tasks too: cancellation can fault I/O between WaitAsync
                // returning and this finally block checking its completion state.
                _ = DrainProbeAsync(pending, scope!, request!, slot);
            }
            else
            {
                ReleaseProbeResources(scope, request, slot);
            }
        }
    }

    private void Apply(PhysicalDeviceDescriptor descriptor, PhysicalDeviceReadinessObservation observation) =>
        _store.Apply(descriptor, observation, _options.ReadyStabilityWindow, _options.RequireFullPreflightForReady, _timeProvider.GetUtcNow());

    private async Task DrainProbeAsync(Task pending, IServiceScope scope, CancellationTokenSource request, DeviceProbeSlot slot)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Observe abandoned I/O only. */ }
        finally
        {
            ReleaseProbeResources(scope, request, slot);
        }
    }

    private void ReleaseProbeResources(IServiceScope? scope, CancellationTokenSource? request, DeviceProbeSlot slot)
    {
        try { request?.Dispose(); scope?.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { _logger.LogWarning(ex, "Readiness probe resource disposal failed."); }
        finally { CompleteDeviceRefresh(slot); }
    }

    private void CompleteDeviceRefresh(DeviceProbeSlot slot)
    {
        _store.CompleteRefresh(_timeProvider.GetUtcNow());
        slot.Gate.Release();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => !_enabled
        ? Task.CompletedTask
        : Task.WhenAll(_descriptors.Select(d => PollDeviceAsync(d, stoppingToken)));

    private async Task PollDeviceAsync(PhysicalDeviceDescriptor descriptor, CancellationToken stoppingToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _shutdown.Token);
        var interval = _options.PollInterval > TimeSpan.Zero ? _options.PollInterval : TimeSpan.FromSeconds(2);
        var first = true;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await RefreshDeviceAsync(descriptor, first, stop.Token);
                first = false;
                await Task.Delay(interval, _timeProvider, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogError(exception, "Readiness loop for {DeviceId} failed; retrying after its poll interval.", descriptor.DeviceId);
                try { await Task.Delay(interval, _timeProvider, stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _shutdown.Cancel();
        base.Dispose();
    }

    private sealed class DeviceProbeSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int FullRequested;
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

    private string BuildConfigurationFingerprint() => string.Join(
        '\u001f',
        _enabled,
        _options.Enabled,
        _options.PollInterval.Ticks,
        _options.ReadyStabilityWindow.Ticks,
        _options.FullPreflightInterval.Ticks,
        _options.ObservationStaleAfter.Ticks,
        _options.ProbeTimeout.Ticks,
        _options.RequireFullPreflightForReady,
        _profile.Features.UseSimulator);
}
