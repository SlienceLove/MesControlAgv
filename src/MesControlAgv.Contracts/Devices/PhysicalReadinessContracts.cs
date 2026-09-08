using System.Text.Json.Serialization;

namespace MesControlAgv.Contracts;

/// <summary>
/// Unified, fail-closed state used by the physical-device supervisor.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PhysicalDeviceReadinessState
{
    Unknown,
    Offline,
    Stabilizing,
    Ready,
    Blocked,
    Degraded
}

/// <summary>
/// Stable reason codes surfaced by the supervisor.  Vendor-specific details
/// remain in <see cref="PhysicalDeviceReadinessSnapshot.LastError"/>.
/// </summary>
public static class PhysicalReadinessReasonCodes
{
    public const string SupervisorDisabled = "physical_readiness_supervisor_disabled";
    public const string SimulatorProfile = "physical_readiness_disabled_for_simulator";
    public const string SupervisorInstanceRequired = "readiness_supervisor_instance_required";
    public const string SupervisorInstanceMismatch = "readiness_supervisor_instance_mismatch";
    public const string NotObserved = "physical_device_not_observed";
    public const string ProbeUnavailable = "physical_readiness_probe_unavailable";
    public const string ObservationStale = "physical_readiness_observation_stale";
    public const string DeviceOffline = "device_offline";
    public const string FullPreflightPending = "full_preflight_pending";
    public const string FullPreflightUnavailable = "full_preflight_unavailable";
    public const string FullPreflightRejected = "full_preflight_rejected";
    public const string ActiveTask = "device_has_active_task";
    public const string TemporaryObstacle = "temporary_obstacle_detected";
    public const string EpochRequired = "device_epoch_required";
    public const string EpochMismatch = "device_epoch_mismatch";
    public const string DeviceNotReady = "device_not_ready";
    public const string ReauthorizationRequired = "device_reauthorization_required";
    public const string ProbeNotRegistered = "physical_readiness_probe_not_registered";
    public const string IdentityChanged = "device_identity_or_map_changed";
}

/// <summary>
/// Device metadata supplied to an extensible read-only readiness probe.
/// Endpoint and protocol details intentionally do not cross this boundary.
/// </summary>
public sealed record PhysicalDeviceDescriptor(
    string DeviceId,
    string DeviceFamily,
    bool RequiredForScheduling,
    bool Enabled = true,
    bool ControlEnabled = false);

/// <summary>
/// Normalized result returned by one physical read-only probe.  A null Online
/// means that connectivity could not be established or trusted; it is not
/// silently converted to an offline device.
/// </summary>
public sealed record PhysicalDeviceReadinessObservation
{
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceFamily { get; init; } = string.Empty;
    public bool ProbeSucceeded { get; init; }
    public bool? Online { get; init; }
    public string? CurrentStationId { get; init; }
    public Guid? ActiveTaskId { get; init; }
    public string? ActiveTaskState { get; init; }
    public string? ActiveDeviceTaskId { get; init; }
    public string? ActiveTaskTargetStationId { get; init; }
    public string? ActiveTaskError { get; init; }
    public string? ControlOwner { get; init; }
    public string? MapName { get; init; }
    public string? MapVersion { get; init; }
    public string? MapMd5 { get; init; }
    public double? LocalizationConfidence { get; init; }
    public bool? Emergency { get; init; }
    public bool? Blocked { get; init; }
    public int? FatalCount { get; init; }
    public int? ErrorCount { get; init; }
    public int? RelocationStatus { get; init; }
    public string? VehicleOperatingMode { get; init; }
    public string? VehicleModel { get; init; }
    public string? ControllerVersion { get; init; }
    public string? RobotMode { get; init; }
    public string? SafetyMode { get; init; }
    public string? RuntimeState { get; init; }
    public string? OperationalMode { get; init; }
    public string? LoadedProgram { get; init; }
    public bool IsFullPreflight { get; init; }
    public bool FullPreflightPassed { get; init; }
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FullPreflightBlockingReasons { get; init; } = Array.Empty<string>();
    public DateTimeOffset ObservedAtUtc { get; init; }
    public DateTimeOffset? FullPreflightObservedAtUtc { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Operator-facing immutable state for one configured physical device.
/// DeviceEpoch changes whenever a previously trusted device session must no
/// longer be treated as the same physical session (power cycle, reconnect,
/// identity/map change, or loss of trusted observation).
/// </summary>
public sealed record PhysicalDeviceReadinessSnapshot
{
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceFamily { get; init; } = string.Empty;
    public bool RequiredForScheduling { get; init; }
    public bool Enabled { get; init; }
    public bool ControlEnabled { get; init; }
    public PhysicalDeviceReadinessState State { get; init; }
    public long DeviceEpoch { get; init; }
    public bool Online { get; init; }
    public bool ProbeSucceeded { get; init; }
    public bool FullPreflightValid { get; init; }
    public bool RequiresReauthorization { get; init; } = true;
    public string? CurrentStationId { get; init; }
    public Guid? ActiveTaskId { get; init; }
    public string? ActiveTaskState { get; init; }
    public string? ActiveDeviceTaskId { get; init; }
    public string? ActiveTaskTargetStationId { get; init; }
    public string? ActiveTaskError { get; init; }
    public string? ControlOwner { get; init; }
    public string? MapName { get; init; }
    public string? MapVersion { get; init; }
    public string? MapMd5 { get; init; }
    public double? LocalizationConfidence { get; init; }
    public bool? Emergency { get; init; }
    public bool? Blocked { get; init; }
    public int? FatalCount { get; init; }
    public int? ErrorCount { get; init; }
    public int? RelocationStatus { get; init; }
    public string? VehicleOperatingMode { get; init; }
    public string? VehicleModel { get; init; }
    public string? ControllerVersion { get; init; }
    public string? RobotMode { get; init; }
    public string? SafetyMode { get; init; }
    public string? RuntimeState { get; init; }
    public string? OperationalMode { get; init; }
    public string? LoadedProgram { get; init; }
    public DateTimeOffset? ReadySinceUtc { get; init; }
    public DateTimeOffset? OfflineSinceUtc { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public DateTimeOffset? LastFullPreflightAtUtc { get; init; }
    public string? LastError { get; init; }
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();
    public bool IsReadOnly { get; init; } = true;
}

/// <summary>
/// Aggregate read-only view returned by MES and consumed by WPF/schedulers.
/// SchedulingPermitted is deliberately stricter than an individual device's
/// State: a current epoch-bound authorization is still required.
/// </summary>
public sealed record PhysicalReadinessResponse
{
    public string SchemaVersion { get; init; } = "mes.physical-readiness/1.0";
    public bool Enabled { get; init; }
    public bool ReadOnly { get; init; } = true;
    public string SupervisorInstanceId { get; init; } = string.Empty;
    public DateTimeOffset ObservedAtUtc { get; init; }
    public bool RefreshInProgress { get; init; }
    public bool SchedulingPermitted { get; init; }
    public IReadOnlyList<PhysicalDeviceReadinessSnapshot> Devices { get; init; } = Array.Empty<PhysicalDeviceReadinessSnapshot>();
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();
}

/// <summary>Optional query/body shape for a manual, still read-only refresh.</summary>
public sealed record PhysicalReadinessRefreshRequest(bool ForceFull = true);
