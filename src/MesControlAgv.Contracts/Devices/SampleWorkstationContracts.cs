using System.Text.Json;
using System.Text.Json.Serialization;
using MesControlAgv.Contracts.Devices;

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
    UpdateTaskBarcodes,
    ImportTasks
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

/// <summary>
/// Stable MES-to-Adapter command envelope. Vendor HTTP fields stay inside the
/// Adapter; callers provide only the approved task identity and template data.
/// </summary>
public record SampleWorkstationOperationRequest
{
    public Guid OperationId { get; init; }
    public Guid RunId { get; init; }
    public Guid NodeExecutionId { get; init; }
    public string OperatorName { get; init; } = string.Empty;
    /// <summary>Optional custody key used by MES to append a sample event.</summary>
    public string? SampleId { get; init; }

    public bool IsValid(out string error)
    {
        if (OperationId == Guid.Empty || RunId == Guid.Empty || NodeExecutionId == Guid.Empty)
        {
            error = "OperationId, RunId and NodeExecutionId are required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(OperatorName))
        {
            error = "OperatorName is required.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

public sealed record SampleWorkstationTaskCreateRequest : SampleWorkstationOperationRequest
{
    public string TaskNo { get; init; } = string.Empty;
    public string TaskName { get; init; } = string.Empty;
    public string TemplateVersion { get; init; } = string.Empty;
}

public sealed record SampleWorkstationTrajectoryRequest : SampleWorkstationOperationRequest
{
    public string TaskNo { get; init; } = string.Empty;
    public int RecordNumber { get; init; }
    public string LiquidCode { get; init; } = string.Empty;
    public string LiquidName { get; init; } = string.Empty;
    public string WarehouseLocation { get; init; } = string.Empty;
    public int WarehouseX { get; init; }
    public int WarehouseY { get; init; }
    public string SourceBarCode { get; init; } = string.Empty;
    public int SourceX { get; init; }
    public int SourceY { get; init; }
    public string TargetBarCode { get; init; } = string.Empty;
    public int TargetX { get; init; }
    public int TargetY { get; init; }
    public int TransferVolume { get; init; }
}

public sealed record SampleWorkstationStartRequest : SampleWorkstationOperationRequest
{
    public string TaskNo { get; init; } = string.Empty;
}

public sealed record SampleWorkstationOperationResponse(
    string DeviceId,
    Guid OperationId,
    Guid RunId,
    Guid NodeExecutionId,
    DeviceOperationLifecycle Status,
    UnknownReason? UnknownReason,
    string? VendorTaskId,
    string? VendorMessage,
    DateTimeOffset ObservedAtUtc)
{
    public bool IsTerminal => Status is DeviceOperationLifecycle.Completed
        or DeviceOperationLifecycle.Failed
        or DeviceOperationLifecycle.Cancelled
        or DeviceOperationLifecycle.Unknown
        or DeviceOperationLifecycle.ManualInterventionRequired;
}

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
    DateTimeOffset ObservedAtUtc)
{
    public bool ReadbackVerified { get; init; }
    public string? FileSha256 { get; init; }
    public SampleWorkstationTaskTemplate? Template { get; init; }
}

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
    public const string TemplateMismatch = "workstation_template_mismatch";
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
