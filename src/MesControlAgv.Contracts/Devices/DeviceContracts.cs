namespace MesControlAgv.Contracts;

public sealed record AgvTaskResponse(
    Guid TaskId,
    string DeviceTaskId,
    string TargetStationId,
    string State,
    string? LastError,
    string AgvId = "AGV-01",
    IReadOnlyList<string>? Path = null);

public sealed record AgvCapabilitiesResponse(
    bool SupportsPause,
    bool SupportsResume,
    bool SupportsCancel,
    bool SupportsEmergencyStop,
    bool SupportsLift,
    bool SupportsBarcode,
    bool SupportsStationConfirmation)
{
    public static AgvCapabilitiesResponse Standard { get; } = new(
        SupportsPause: true,
        SupportsResume: true,
        SupportsCancel: true,
        SupportsEmergencyStop: false,
        SupportsLift: false,
        SupportsBarcode: false,
        SupportsStationConfirmation: true);
}

/// <summary>
/// Raw and normalized safety facts observed from a physical controller. Unknown
/// values are intentional: callers must fail closed rather than infer a mode.
/// </summary>
public sealed record AgvSafetyReadinessResponse(
    string VehicleOperatingMode,
    string? VehicleOperatingModeSource,
    string? MapName,
    string? MapMd5,
    bool? ForkAutomatic,
    int? DispatchMode,
    bool? ManualBlock,
    bool? SrcRelease,
    bool? Emergency,
    bool? Blocked,
    int FatalCount,
    int ErrorCount,
    int? RelocationStatus,
    double? LocalizationConfidence,
    DateTimeOffset ObservedAtUtc);

public sealed record AgvSnapshotResponse(
    bool Online,
    string ControlOwner,
    string? CurrentStationId,
    Guid? CurrentTaskId,
    string AgvId = "AGV-01",
    AgvCapabilitiesResponse? Capabilities = null,
    AgvSafetyReadinessResponse? SafetyReadiness = null);

/// <summary>
/// Read-only correlation of an AGV's MES transport task with its current
/// Adapter/device operation. A null DeviceState means the Adapter did not
/// return an operation status during this snapshot.
/// </summary>
public sealed record AgvActiveTaskStatusResponse(
    Guid TransportTaskId,
    Guid OperationId,
    string MesStatus,
    string? DeviceTaskId,
    string? DeviceState,
    string? TargetStationId,
    string? LastError,
    IReadOnlyList<string>? Path);

/// <summary>
/// Fleet snapshot enriched with the active MES task assigned to each AGV.
/// This endpoint is read-only and does not reconcile or control the device.
/// </summary>
public sealed record AgvFleetStatusResponse(
    AgvSnapshotResponse Snapshot,
    AgvActiveTaskStatusResponse? ActiveTask);

/// <summary>
/// Read-only physical-dispatch assessment. A false result never acquires control
/// and never sends a navigation command.
/// </summary>
public sealed record PhysicalAgvPreflightResponse(
    AgvSnapshotResponse Snapshot,
    AgvSafetyReadinessResponse? Readiness,
    bool DispatchPermitted,
    IReadOnlyList<string> BlockingReasons);

public sealed record AgvCommandRequest(
    string Command,
    Guid? TaskId = null);
