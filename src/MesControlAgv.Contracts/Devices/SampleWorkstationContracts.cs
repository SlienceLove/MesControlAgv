using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesControlAgv.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SampleWorkstationDeviceState
{
    Unknown,
    Idle,
    Running,
    Paused,
    Faulted,
    Initializing,
    Offline
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SampleWorkstationTaskState
{
    Unknown,
    Waiting,
    Running,
    Completed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SampleWorkstationCommandOperation
{
    Initialize,
    StartTask,
    UpdateTaskBarcodes
}

/// <summary>V1.02 identities for the two source bottles, not module/rack codes.</summary>
public sealed record SampleWorkstationTaskBarcodes
{
    public string SampleBarcode1 { get; init; } = string.Empty;
    public string SampleBarcode2 { get; init; } = string.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SampleWorkstationProtocolOperation
{
    WorkflowList,
    WorkflowDetails,
    ExperimentalTaskTemplate,
    WorkflowTemplate,
    MaterialTypeList,
    MaterialTypeParameterList,
    MaterialTypeParameterDetails,
    MaterialTemplate,
    PlatformLayoutList,
    PlatformLayoutDetails,
    PlatformLayoutTemplate,
    TrajectoryParameterList,
    TrajectoryParameterDetails,
    TrajectoryParameterTemplate,
    SolventParameterList,
    SolventParameterDetails,
    SolventParameterTemplate
}

public sealed record SampleWorkstationStatusResponse(
    string DeviceId,
    string EquipmentNo,
    bool Online,
    SampleWorkstationDeviceState State,
    int RawState,
    DateTimeOffset ObservedAtUtc);

public sealed record SampleWorkstationErrorResponse(
    string DeviceId,
    int? ErrorCode,
    string Description,
    bool Recognized,
    DateTimeOffset ObservedAtUtc)
{
    public JsonElement? RawData { get; init; }
}

public sealed record SampleWorkstationTaskSummaryResponse(
    int RecordNumber,
    string TaskNo,
    string TaskName,
    SampleWorkstationTaskState State,
    string RawState,
    string MakeTime,
    string? Remark);

public sealed record SampleWorkstationTaskDetailsResponse(
    int RecordNumber,
    string TaskNo,
    string TaskName,
    SampleWorkstationTaskState State,
    string RawState,
    string? RequestTime,
    string? ProductionTime,
    string? CompletionTime,
    string OperatorAccount,
    string MakeTime,
    string? Remark);

public sealed record SampleWorkstationTaskStateResponse(
    string DeviceId,
    string TaskNo,
    SampleWorkstationTaskState State,
    string RawState,
    DateTimeOffset ObservedAtUtc);

public sealed record SampleWorkstationCommandResponse(
    string DeviceId,
    SampleWorkstationCommandOperation Operation,
    int Code,
    JsonElement Data,
    DateTimeOffset ObservedAtUtc)
{
    public string? TaskNo { get; init; }
    // A command acknowledgement is not task completion. Poll task state separately.
    public bool Acknowledged { get; init; }
}

public sealed record SampleWorkstationCapabilitiesResponse(
    string DeviceId,
    bool Enabled,
    bool ControlEnabled,
    bool TaskImportSupported,
    IReadOnlyList<SampleWorkstationCommandOperation> Commands,
    IReadOnlyList<SampleWorkstationProtocolOperation> ProtocolReads)
{
    // This endpoint describes configuration, not live readiness or execution proof.
    public string Source => "AdapterConfiguration";
}

public sealed record SampleWorkstationTaskImportResponse(
    string DeviceId,
    IReadOnlyList<string> TaskNos,
    DateTimeOffset ObservedAtUtc);

public static class SampleWorkstationErrorCodes
{
    public const string Disabled = "workstation_disabled";
    public const string ControlDisabled = "workstation_control_disabled";
    public const string InvalidRequest = "workstation_invalid_request";
    public const string NotFound = "workstation_not_found";
    public const string UnsupportedOperation = "workstation_unsupported_operation";
    public const string VendorFailure = "workstation_vendor_failure";
    public const string InvalidPayload = "workstation_invalid_payload";
    public const string CommandUnconfirmed = "workstation_command_unconfirmed";
    public const string CommandRejected = "workstation_command_rejected";
    public const string Timeout = "workstation_timeout";
    public const string Unavailable = "workstation_unavailable";
}

public sealed record SampleWorkstationProtocolReadQuery(
    string? Key = null,
    string? StartDate = null,
    string? EndDate = null,
    int StartNo = 1,
    int RecordNum = 50);

public sealed record SampleWorkstationProtocolResponse(
    string DeviceId,
    SampleWorkstationProtocolOperation Operation,
    int Code,
    JsonElement Data,
    DateTimeOffset ObservedAtUtc);

public sealed record SampleWorkstationTaskQuery(
    string? State = null,
    string? StartDate = null,
    string? EndDate = null,
    int StartNo = 1,
    int RecordNum = 50);
