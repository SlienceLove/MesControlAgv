namespace MesControlAgv.Contracts;

public sealed record AgvTaskResponse(
    Guid TaskId,
    string DeviceTaskId,
    string TargetStationId,
    string State,
    string? LastError,
    string AgvId = "AGV-01",
    IReadOnlyList<string>? Path = null);

/// <summary>Read-only Adapter process identity; it contains no controller state.</summary>
public sealed record AdapterRuntimeIdentityResponse(
    string Service,
    string Status,
    string RunMode,
    string Driver,
    IReadOnlyList<AdapterModuleIdentityResponse>? Modules = null,
    IReadOnlyList<AdapterDeviceIdentityResponse>? Devices = null);

/// <summary>Read-only description of a device-family module loaded by the Adapter host.</summary>
public sealed record AdapterModuleIdentityResponse(
    string ModuleId,
    string DeviceType,
    IReadOnlyList<string> SupportedTransports);

/// <summary>Read-only coarse policy and driver identity for one configured device.</summary>
public sealed record AdapterDeviceIdentityResponse(
    string DeviceId,
    string DeviceType,
    string ModuleId,
    string DriverId,
    string Transport,
    bool Enabled,
    bool ControlEnabled);

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
    DateTimeOffset ObservedAtUtc,
    string? VehicleModel = null,
    string? ControllerVersion = null);

/// <summary>
/// A fresh, controller-authored map catalog observed through a read-only vendor
/// API or a vendor-approved read-only export. A local .smap file is not
/// controller evidence.
/// </summary>
public sealed record ControllerMapEvidenceResponse(
    bool IsControllerAuthoritative,
    string? Source,
    string? MapName,
    string? Version,
    string? Md5,
    IReadOnlyList<string>? StationIds,
    IReadOnlyList<ControllerDirectedEdgeResponse>? DirectedEdges,
    DateTimeOffset ObservedAtUtc);

public sealed record ControllerDirectedEdgeResponse(string From, string To);

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
    IReadOnlyList<string> BlockingReasons,
    ControllerMapEvidenceResponse? MapEvidence = null,
    string? VehicleOperatingModePolicy = null);

public sealed record AgvCommandRequest(
    string Command,
    Guid? TaskId = null);

/// <summary>
/// Normalized read-only CIC-D160+ observation returned through MES. Candidate
/// fields remain nullable and carry their mapping confidence explicitly.
/// </summary>
public sealed record IonChromatographyStatusResponse(
    string InstrumentId,
    string Model,
    string SerialNumber,
    bool Online,
    string DeviceState,
    bool PortOwned,
    DateTimeOffset ObservedAtUtc,
    double? Pressure = null,
    double? ColumnTemperature = null,
    double? DetectorTemperature = null,
    string? Alarm = null,
    double? Conductivity = null,
    double? TotalConductivity = null,
    double? Flow = null,
    string? MappingConfidence = null);

/// <summary>
/// Control-center projection of instrument status and the currently enforced
/// operation policy. Task admission stays false until write commands are
/// independently evidenced and authorized.
/// </summary>
public sealed record IonChromatographyControlCenterStatusResponse(
    IonChromatographyStatusResponse Status,
    bool TaskAdmissionEnabled,
    IReadOnlyList<string> EnabledOperations,
    string ControlPolicy);
