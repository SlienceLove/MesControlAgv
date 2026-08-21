namespace MesControlAgv.Mes.Entities;

public sealed class WorkflowDeviceOperationRecord
{
    public Guid OperationId { get; set; }

    public Guid WorkflowRunId { get; set; }

    public Guid NodeExecutionId { get; set; }

    public Guid RequestId { get; set; }

    public int Attempt { get; set; }

    public string CapabilityId { get; set; } = string.Empty;

    public string? DeviceId { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public string? CorrelationId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string RequestSummaryJson { get; set; } = "{}";

    public string ResultSummaryJson { get; set; } = "{}";

    public DateTime RequestedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public DateTime? ReconciledAtUtc { get; set; }

    public string? LastError { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
