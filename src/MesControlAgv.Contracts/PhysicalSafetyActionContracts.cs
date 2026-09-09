using System.Text.Json.Serialization;

namespace MesControlAgv.Contracts;

public static class PhysicalSafetyActionTypes
{
    public const string AgvRelease = "agv_release";
    public const string AuboStop = "aubo_stop";
}

public static class PhysicalSafetyActionStatuses
{
    public const string Prepared = "Prepared";
    public const string Succeeded = "Succeeded";
    public const string Rejected = "Rejected";
    public const string Unknown = "Unknown";
}

public sealed record PhysicalAgvReleaseRequest(
    Guid RequestId,
    string OperatorName,
    string Reason,
    Guid? WorkflowRunId = null,
    Guid? WorkflowNodeExecutionId = null,
    Guid? WorkflowDeviceOperationId = null,
    long? DeviceEpoch = null,
    string? SupervisorInstanceId = null);

public sealed record PhysicalAuboStopRequest(
    Guid RequestId,
    Guid OperationId,
    string OperatorName,
    AuboArmOperationCorrelation? Correlation = null,
    string? Reason = null);

public sealed record PhysicalSafetyActionResponse(
    Guid Id,
    Guid RequestId,
    string Fingerprint,
    string ActionType,
    string DeviceId,
    string OperatorName,
    string Reason,
    string Status,
    string? ResultSummary,
    Guid? WorkflowRunId,
    Guid? WorkflowNodeExecutionId,
    Guid? WorkflowDeviceOperationId,
    string? CorrelationId,
    DateTimeOffset PreparedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    [JsonIgnore]
    public bool IsTerminal => Status is
        PhysicalSafetyActionStatuses.Succeeded or
        PhysicalSafetyActionStatuses.Rejected or
        PhysicalSafetyActionStatuses.Unknown;
}
