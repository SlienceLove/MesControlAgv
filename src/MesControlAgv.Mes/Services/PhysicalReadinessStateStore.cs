using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Configuration for the permanent, read-only physical-device supervisor.
/// The feature is opt-in so a normal build or wireless-only capture cannot
/// unexpectedly open additional controller channels.
/// </summary>
public sealed class PhysicalReadinessSupervisorOptions
{
    public const string SectionName = "PhysicalReadinessSupervisor";

    public bool Enabled { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ReadyStabilityWindow { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan FullPreflightInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ObservationStaleAfter { get; init; } = TimeSpan.FromSeconds(10);
    public bool RequireFullPreflightForReady { get; init; } = true;
}

/// <summary>
/// Thread-safe in-memory projection of physical readiness.  It intentionally
/// contains no controller client and therefore cannot issue a device command.
/// </summary>
public sealed class PhysicalReadinessStateStore : IPhysicalReadinessState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceEntry> _devices =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _instanceId;
    private readonly TimeProvider _timeProvider;
    private bool _enabled;
    private bool _refreshInProgress;
    private DateTimeOffset _lastRefreshAtUtc;
    private TimeSpan _observationStaleAfter = TimeSpan.FromSeconds(10);
    private string _disabledReason = PhysicalReadinessReasonCodes.SupervisorDisabled;

    public PhysicalReadinessStateStore(
        TimeProvider? timeProvider = null,
        string? instanceId = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _instanceId = string.IsNullOrWhiteSpace(instanceId)
            ? Guid.NewGuid().ToString("N")
            : instanceId.Trim();
    }

    public bool Enabled
    {
        get
        {
            lock (_gate) return _enabled;
        }
    }

    public void Configure(
        bool enabled,
        IEnumerable<PhysicalDeviceDescriptor> descriptors,
        DateTimeOffset observedAtUtc,
        string? disabledReason = null,
        TimeSpan? observationStaleAfter = null)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var normalizedDescriptors = descriptors
            .Where(device => device.Enabled)
            .Select(NormalizeDescriptor)
            .GroupBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var now = NormalizeTime(observedAtUtc);

        lock (_gate)
        {
            _enabled = enabled;
            _disabledReason = string.IsNullOrWhiteSpace(disabledReason)
                ? PhysicalReadinessReasonCodes.SupervisorDisabled
                : disabledReason.Trim();
            _lastRefreshAtUtc = now;
            _observationStaleAfter = observationStaleAfter is { } configuredStaleness &&
                                     configuredStaleness > TimeSpan.Zero
                ? configuredStaleness
                : TimeSpan.FromSeconds(10);

            var activeIds = normalizedDescriptors
                .Select(device => device.DeviceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var staleId in _devices.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            {
                _devices.Remove(staleId);
            }

            foreach (var descriptor in normalizedDescriptors)
            {
                if (_devices.TryGetValue(descriptor.DeviceId, out var existing))
                {
                    existing.Descriptor = descriptor;
                    existing.Snapshot = existing.Snapshot with
                    {
                        DeviceFamily = descriptor.DeviceFamily,
                        RequiredForScheduling = descriptor.RequiredForScheduling,
                        Enabled = descriptor.Enabled,
                        ControlEnabled = descriptor.ControlEnabled
                    };
                    continue;
                }

                _devices.Add(descriptor.DeviceId, new DeviceEntry
                {
                    Descriptor = descriptor,
                    Snapshot = CreateInitialSnapshot(
                        descriptor,
                        now,
                        enabled
                            ? PhysicalReadinessReasonCodes.NotObserved
                            : _disabledReason)
                });
            }

            if (!enabled)
            {
                foreach (var entry in _devices.Values)
                {
                    entry.StableSinceUtc = null;
                    entry.Snapshot = entry.Snapshot with
                    {
                        State = PhysicalDeviceReadinessState.Unknown,
                        Online = false,
                        ProbeSucceeded = false,
                        RequiresReauthorization = true,
                        ReadySinceUtc = null,
                        ObservedAtUtc = now,
                        BlockingReasons = [_disabledReason]
                    };
                }
            }
        }
    }

    public void SetRefreshInProgress(bool value)
    {
        lock (_gate) _refreshInProgress = value;
    }

    public void CompleteRefresh(DateTimeOffset observedAtUtc)
    {
        lock (_gate)
        {
            _lastRefreshAtUtc = NormalizeTime(observedAtUtc);
            _refreshInProgress = false;
        }
    }

    public bool ShouldRunFullPreflight(
        string deviceId,
        DateTimeOffset now,
        TimeSpan interval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_gate)
        {
            InvalidateStaleEntries(NormalizeTime(now));
            if (!_devices.TryGetValue(deviceId.Trim(), out var entry)) return true;
            if (!entry.HasFullPreflight) return true;
            if (entry.Snapshot.State != PhysicalDeviceReadinessState.Ready) return true;
            if (interval <= TimeSpan.Zero) return true;
            return entry.Snapshot.LastFullPreflightAtUtc is not { } last ||
                   NormalizeTime(now) - last >= interval;
        }
    }

    public void Apply(
        PhysicalDeviceDescriptor descriptor,
        PhysicalDeviceReadinessObservation observation,
        TimeSpan stabilityWindow,
        bool requireFullPreflight,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(observation);
        descriptor = NormalizeDescriptor(descriptor);
        if (!string.Equals(
                descriptor.DeviceId,
                observation.DeviceId?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The readiness observation device id does not match its descriptor.",
                nameof(observation));
        }

        var observedAt = observation.ObservedAtUtc == default
            ? NormalizeTime(now)
            : NormalizeTime(observation.ObservedAtUtc);
        var currentTime = NormalizeTime(now);

        lock (_gate)
        {
            if (!_devices.TryGetValue(descriptor.DeviceId, out var entry))
            {
                entry = new DeviceEntry
                {
                    Descriptor = descriptor,
                    Snapshot = CreateInitialSnapshot(
                        descriptor,
                        currentTime,
                        PhysicalReadinessReasonCodes.NotObserved)
                };
                _devices.Add(descriptor.DeviceId, entry);
            }

            entry.Descriptor = descriptor;
            var previous = entry.Snapshot;
            // Only a complete preflight carries the authoritative identity/map
            // fields.  Ordinary polling intentionally omits some of them; if a
            // partial observation were allowed to rebuild the fingerprint, the
            // epoch would oscillate between the full and partial shapes on
            // every refresh and invalidate an otherwise stable authorization.
            var identity = observation.IsFullPreflight
                ? BuildIdentityFingerprint(observation)
                : null;
            var identityChanged = entry.IdentityFingerprint is not null &&
                                  identity is not null &&
                                  !string.Equals(
                                      entry.IdentityFingerprint,
                                      identity,
                                      StringComparison.OrdinalIgnoreCase);
            var sessionBoundary = !entry.HasObservation ||
                                  entry.LastProbeSucceeded != observation.ProbeSucceeded ||
                                  (entry.LastOnline.HasValue && observation.Online.HasValue &&
                                   entry.LastOnline.Value != observation.Online.Value) ||
                                  identityChanged;

            var epoch = previous.DeviceEpoch;
            if (sessionBoundary || epoch <= 0)
            {
                epoch = checked(Math.Max(0, epoch) + 1);
            }

            if (observation.IsFullPreflight)
            {
                entry.HasFullPreflight = true;
                entry.FullPreflightPassed = observation.FullPreflightPassed;
                entry.FullPreflightBlockingReasons = NormalizeReasons(
                    observation.FullPreflightBlockingReasons.Count > 0
                        ? observation.FullPreflightBlockingReasons
                        : observation.BlockingReasons);
                entry.LastFullPreflightAtUtc = NormalizeTime(
                    observation.FullPreflightObservedAtUtc ?? observedAt);
            }

            var reasons = observation.IsFullPreflight
                ? NormalizeReasons(observation.BlockingReasons).ToList()
                : NormalizeReasons(
                    entry.FullPreflightBlockingReasons.Concat(observation.BlockingReasons)).ToList();

            if (!observation.ProbeSucceeded || !observation.Online.HasValue)
            {
                AddReason(reasons, PhysicalReadinessReasonCodes.ProbeUnavailable);
            }
            else if (!observation.Online.Value)
            {
                AddReason(reasons, PhysicalReadinessReasonCodes.DeviceOffline);
            }

            if (requireFullPreflight && !entry.HasFullPreflight)
            {
                AddReason(reasons, PhysicalReadinessReasonCodes.FullPreflightPending);
            }
            else if (requireFullPreflight && entry.HasFullPreflight &&
                     !entry.FullPreflightPassed && entry.FullPreflightBlockingReasons.Count == 0)
            {
                AddReason(reasons, PhysicalReadinessReasonCodes.FullPreflightRejected);
            }

            if (identityChanged)
            {
                AddReason(reasons, PhysicalReadinessReasonCodes.IdentityChanged);
            }

            var probeTrusted = observation.ProbeSucceeded && observation.Online.HasValue;
            var online = observation.Online == true;
            var fullPreflightSatisfied = !requireFullPreflight ||
                                         (entry.HasFullPreflight && entry.FullPreflightPassed);
            var canStabilize = probeTrusted && online &&
                               fullPreflightSatisfied && reasons.Count == 0;

            // An observed active task or temporary obstacle is a scheduling
            // blocker, not a new controller session. Keeping the epoch lets an
            // authorized multi-step workflow continue after the condition is
            // clear. Every other departure from Ready is fail-closed and
            // invalidates the authorization immediately.
            var transientOperationalBlockersOnly = reasons.Count > 0 && reasons.All(reason =>
                string.Equals(
                    reason,
                    PhysicalReadinessReasonCodes.ActiveTask,
                    StringComparison.Ordinal) ||
                string.Equals(
                    reason,
                    PhysicalReadinessReasonCodes.TemporaryObstacle,
                    StringComparison.Ordinal));
            if (previous.State == PhysicalDeviceReadinessState.Ready &&
                !canStabilize &&
                !sessionBoundary &&
                !transientOperationalBlockersOnly)
            {
                epoch = checked(epoch + 1);
                sessionBoundary = true;
            }

            PhysicalDeviceReadinessState state;
            if (!probeTrusted)
            {
                state = entry.HasObservation
                    ? PhysicalDeviceReadinessState.Degraded
                    : PhysicalDeviceReadinessState.Unknown;
                entry.StableSinceUtc = null;
            }
            else if (!online)
            {
                state = PhysicalDeviceReadinessState.Offline;
                entry.StableSinceUtc = null;
            }
            else if (!canStabilize)
            {
                state = reasons.Count == 1 &&
                        reasons[0] == PhysicalReadinessReasonCodes.FullPreflightPending
                    ? PhysicalDeviceReadinessState.Stabilizing
                    : PhysicalDeviceReadinessState.Blocked;
                entry.StableSinceUtc = null;
            }
            else
            {
                if (sessionBoundary || entry.StableSinceUtc is null ||
                    previous.State is not (PhysicalDeviceReadinessState.Stabilizing or
                        PhysicalDeviceReadinessState.Ready))
                {
                    entry.StableSinceUtc = currentTime;
                }

                state = stabilityWindow <= TimeSpan.Zero ||
                        currentTime - entry.StableSinceUtc.Value >= stabilityWindow
                    ? PhysicalDeviceReadinessState.Ready
                    : PhysicalDeviceReadinessState.Stabilizing;
            }

            var requiresReauthorization = sessionBoundary ||
                                          previous.DeviceEpoch != epoch ||
                                          previous.RequiresReauthorization;
            var mapName = observation.IsFullPreflight
                ? observation.MapName
                : observation.MapName ?? previous.MapName;
            var mapVersion = observation.IsFullPreflight
                ? observation.MapVersion
                : observation.MapVersion ?? previous.MapVersion;
            var mapMd5 = observation.IsFullPreflight
                ? observation.MapMd5
                : observation.MapMd5 ?? previous.MapMd5;

            entry.Snapshot = new PhysicalDeviceReadinessSnapshot
            {
                DeviceId = descriptor.DeviceId,
                DeviceFamily = descriptor.DeviceFamily,
                RequiredForScheduling = descriptor.RequiredForScheduling,
                Enabled = descriptor.Enabled,
                ControlEnabled = descriptor.ControlEnabled,
                State = state,
                DeviceEpoch = epoch,
                Online = online,
                ProbeSucceeded = observation.ProbeSucceeded,
                FullPreflightValid = entry.HasFullPreflight && entry.FullPreflightPassed,
                RequiresReauthorization = requiresReauthorization,
                CurrentStationId = observation.CurrentStationId,
                ActiveTaskId = observation.ActiveTaskId,
                ActiveTaskState = observation.ActiveTaskState,
                ActiveDeviceTaskId = observation.ActiveDeviceTaskId,
                ActiveTaskTargetStationId = observation.ActiveTaskTargetStationId,
                ActiveTaskError = observation.ActiveTaskError,
                ControlOwner = observation.ControlOwner,
                MapName = mapName,
                MapVersion = mapVersion,
                MapMd5 = mapMd5,
                LocalizationConfidence = observation.LocalizationConfidence ?? previous.LocalizationConfidence,
                Emergency = observation.Emergency ?? previous.Emergency,
                Blocked = observation.Blocked ?? previous.Blocked,
                FatalCount = observation.FatalCount ?? previous.FatalCount,
                ErrorCount = observation.ErrorCount ?? previous.ErrorCount,
                RelocationStatus = observation.RelocationStatus ?? previous.RelocationStatus,
                VehicleOperatingMode = observation.VehicleOperatingMode ?? previous.VehicleOperatingMode,
                VehicleModel = observation.VehicleModel ?? previous.VehicleModel,
                ControllerVersion = observation.ControllerVersion ?? previous.ControllerVersion,
                RobotMode = observation.RobotMode ?? previous.RobotMode,
                SafetyMode = observation.SafetyMode ?? previous.SafetyMode,
                RuntimeState = observation.RuntimeState ?? previous.RuntimeState,
                OperationalMode = observation.OperationalMode ?? previous.OperationalMode,
                LoadedProgram = observation.LoadedProgram ?? previous.LoadedProgram,
                ReadySinceUtc = state == PhysicalDeviceReadinessState.Ready
                    ? entry.StableSinceUtc
                    : null,
                OfflineSinceUtc = state == PhysicalDeviceReadinessState.Offline
                    ? previous.State == PhysicalDeviceReadinessState.Offline
                        ? previous.OfflineSinceUtc ?? observedAt
                        : observedAt
                    : null,
                ObservedAtUtc = observedAt,
                LastFullPreflightAtUtc = entry.LastFullPreflightAtUtc,
                LastError = string.IsNullOrWhiteSpace(observation.Error)
                    ? null
                    : observation.Error.Trim(),
                BlockingReasons = reasons.ToArray(),
                IsReadOnly = true
            };

            entry.HasObservation = true;
            entry.LastProbeSucceeded = observation.ProbeSucceeded;
            entry.LastOnline = observation.Online;
            if (identity is not null) entry.IdentityFingerprint = identity;
            _lastRefreshAtUtc = currentTime;
        }
    }

    public PhysicalReadinessResponse GetSnapshot()
    {
        lock (_gate)
        {
            InvalidateStaleEntries(_timeProvider.GetUtcNow());
            var devices = _devices.Values
                .Select(entry => entry.Snapshot)
                .OrderBy(device => device.DeviceFamily, StringComparer.OrdinalIgnoreCase)
                .ThenBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var required = devices.Where(device => device.RequiredForScheduling).ToArray();
            var aggregateReasons = new List<string>();
            if (!_enabled)
            {
                aggregateReasons.Add(_disabledReason);
            }
            else if (required.Length == 0)
            {
                aggregateReasons.Add("no_physical_scheduling_devices_configured");
            }
            else
            {
                foreach (var device in required)
                {
                    if (device.State != PhysicalDeviceReadinessState.Ready)
                    {
                        aggregateReasons.Add(
                            $"{device.DeviceId}:{PhysicalReadinessReasonCodes.DeviceNotReady}");
                    }
                    if (device.RequiresReauthorization)
                    {
                        aggregateReasons.Add(
                            $"{device.DeviceId}:{PhysicalReadinessReasonCodes.ReauthorizationRequired}");
                    }
                }
            }

            var schedulingPermitted = _enabled && required.Length > 0 &&
                                      required.All(device =>
                                          device.State == PhysicalDeviceReadinessState.Ready &&
                                          !device.RequiresReauthorization);
            return new PhysicalReadinessResponse
            {
                Enabled = _enabled,
                ReadOnly = true,
                SupervisorInstanceId = _instanceId,
                ObservedAtUtc = _lastRefreshAtUtc,
                RefreshInProgress = _refreshInProgress,
                SchedulingPermitted = schedulingPermitted,
                Devices = devices,
                BlockingReasons = aggregateReasons.Distinct(StringComparer.Ordinal).ToArray()
            };
        }
    }

    public bool TryGetDevice(
        string deviceId,
        out PhysicalDeviceReadinessSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_gate)
        {
            InvalidateStaleEntries(_timeProvider.GetUtcNow());
            if (_devices.TryGetValue(deviceId.Trim(), out var entry))
            {
                snapshot = entry.Snapshot;
                return true;
            }

            snapshot = new PhysicalDeviceReadinessSnapshot();
            return false;
        }
    }

    public bool IsCurrentAndReady(
        string deviceId,
        long? expectedEpoch,
        out string? reason) =>
        IsCurrentAndReady(deviceId, expectedEpoch, _instanceId, out reason);

    public bool IsCurrentAndReady(
        string deviceId,
        long? expectedEpoch,
        string? expectedSupervisorInstanceId,
        out string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_gate)
        {
            InvalidateStaleEntries(_timeProvider.GetUtcNow());
            if (!_enabled)
            {
                reason = null;
                return true;
            }

            if (string.IsNullOrWhiteSpace(expectedSupervisorInstanceId))
            {
                reason = PhysicalReadinessReasonCodes.SupervisorInstanceRequired;
                return false;
            }

            if (!string.Equals(
                    expectedSupervisorInstanceId.Trim(),
                    _instanceId,
                    StringComparison.Ordinal))
            {
                reason = PhysicalReadinessReasonCodes.SupervisorInstanceMismatch;
                return false;
            }

            if (!_devices.TryGetValue(deviceId.Trim(), out var entry))
            {
                reason = PhysicalReadinessReasonCodes.NotObserved;
                return false;
            }

            if (!expectedEpoch.HasValue || expectedEpoch.Value <= 0)
            {
                reason = PhysicalReadinessReasonCodes.EpochRequired;
                return false;
            }

            if (entry.Snapshot.DeviceEpoch != expectedEpoch.Value)
            {
                reason = PhysicalReadinessReasonCodes.EpochMismatch;
                return false;
            }

            if (entry.Snapshot.State != PhysicalDeviceReadinessState.Ready)
            {
                reason = PhysicalReadinessReasonCodes.DeviceNotReady;
                return false;
            }

            if (entry.Snapshot.RequiresReauthorization)
            {
                reason = PhysicalReadinessReasonCodes.ReauthorizationRequired;
                return false;
            }

            reason = null;
            return true;
        }
    }

    public bool AcknowledgeAuthorization(string deviceId, long expectedEpoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (expectedEpoch <= 0) return false;
        lock (_gate)
        {
            InvalidateStaleEntries(_timeProvider.GetUtcNow());
            if (!_enabled) return true;
            if (!_devices.TryGetValue(deviceId.Trim(), out var entry) ||
                entry.Snapshot.DeviceEpoch != expectedEpoch ||
                entry.Snapshot.State != PhysicalDeviceReadinessState.Ready)
            {
                return false;
            }

            entry.Snapshot = entry.Snapshot with { RequiresReauthorization = false };
            return true;
        }
    }

    private static PhysicalDeviceDescriptor NormalizeDescriptor(PhysicalDeviceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.DeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.DeviceFamily);
        return descriptor with
        {
            DeviceId = descriptor.DeviceId.Trim(),
            DeviceFamily = descriptor.DeviceFamily.Trim()
        };
    }

    private static PhysicalDeviceReadinessSnapshot CreateInitialSnapshot(
        PhysicalDeviceDescriptor descriptor,
        DateTimeOffset observedAtUtc,
        string reason) => new()
        {
            DeviceId = descriptor.DeviceId,
            DeviceFamily = descriptor.DeviceFamily,
            RequiredForScheduling = descriptor.RequiredForScheduling,
            Enabled = descriptor.Enabled,
            ControlEnabled = descriptor.ControlEnabled,
            State = PhysicalDeviceReadinessState.Unknown,
            DeviceEpoch = 0,
            Online = false,
            ProbeSucceeded = false,
            FullPreflightValid = false,
            RequiresReauthorization = true,
            ObservedAtUtc = observedAtUtc,
            BlockingReasons = [reason],
            IsReadOnly = true
        };

    private static IReadOnlyList<string> NormalizeReasons(IEnumerable<string> reasons) =>
        reasons
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Select(reason => reason.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static void AddReason(ICollection<string> reasons, string reason)
    {
        if (!reasons.Contains(reason, StringComparer.Ordinal)) reasons.Add(reason);
    }

    private static string? BuildIdentityFingerprint(PhysicalDeviceReadinessObservation observation)
    {
        var values = new[]
        {
            observation.VehicleModel,
            observation.ControllerVersion,
            observation.MapName,
            observation.MapVersion,
            observation.MapMd5
        };
        if (values.All(string.IsNullOrWhiteSpace)) return null;
        return string.Join('\u001f', values.Select(value => value?.Trim() ?? string.Empty));
    }

    private static DateTimeOffset NormalizeTime(DateTimeOffset value) =>
        value.ToUniversalTime();

    private void InvalidateStaleEntries(DateTimeOffset now)
    {
        if (!_enabled || _observationStaleAfter <= TimeSpan.Zero) return;
        var normalizedNow = NormalizeTime(now);
        foreach (var entry in _devices.Values)
        {
            var snapshot = entry.Snapshot;
            if (snapshot.State is not (PhysicalDeviceReadinessState.Ready or
                    PhysicalDeviceReadinessState.Stabilizing) ||
                snapshot.ObservedAtUtc == default ||
                normalizedNow - snapshot.ObservedAtUtc <= _observationStaleAfter)
            {
                continue;
            }

            var reasons = snapshot.BlockingReasons.ToList();
            AddReason(reasons, PhysicalReadinessReasonCodes.ObservationStale);
            entry.StableSinceUtc = null;
            entry.LastProbeSucceeded = false;
            entry.Snapshot = snapshot with
            {
                State = PhysicalDeviceReadinessState.Degraded,
                DeviceEpoch = checked(Math.Max(0, snapshot.DeviceEpoch) + 1),
                ProbeSucceeded = false,
                RequiresReauthorization = true,
                ReadySinceUtc = null,
                LastError = "The physical readiness observation exceeded the configured freshness window.",
                BlockingReasons = reasons.ToArray()
            };
        }
    }

    private sealed class DeviceEntry
    {
        public required PhysicalDeviceDescriptor Descriptor { get; set; }
        public required PhysicalDeviceReadinessSnapshot Snapshot { get; set; }
        public bool HasObservation { get; set; }
        public bool LastProbeSucceeded { get; set; }
        public bool? LastOnline { get; set; }
        public bool HasFullPreflight { get; set; }
        public bool FullPreflightPassed { get; set; }
        public IReadOnlyList<string> FullPreflightBlockingReasons { get; set; } = Array.Empty<string>();
        public DateTimeOffset? LastFullPreflightAtUtc { get; set; }
        public DateTimeOffset? StableSinceUtc { get; set; }
        public string? IdentityFingerprint { get; set; }
    }
}
