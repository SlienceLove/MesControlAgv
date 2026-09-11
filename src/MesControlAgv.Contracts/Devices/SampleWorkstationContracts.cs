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
    StartTask
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
    DateTimeOffset ObservedAtUtc);

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
    DateTimeOffset ObservedAtUtc);

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
