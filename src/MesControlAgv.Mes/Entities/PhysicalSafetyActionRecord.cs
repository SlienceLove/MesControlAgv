namespace MesControlAgv.Mes.Entities;

public sealed class PhysicalSafetyActionRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RequestId { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public string ActionType { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public string OperatorName { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ResultSummary { get; set; }
    public Guid? WorkflowRunId { get; init; }
    public Guid? WorkflowNodeExecutionId { get; init; }
    public Guid? WorkflowDeviceOperationId { get; init; }
    public string? CorrelationId { get; init; }
    public long? DeviceEpoch { get; init; }
    public string? SupervisorInstanceId { get; init; }
    public DateTimeOffset PreparedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
