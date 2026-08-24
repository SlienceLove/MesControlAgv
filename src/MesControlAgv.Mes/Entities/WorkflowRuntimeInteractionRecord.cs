namespace MesControlAgv.Mes.Entities;

public sealed class WorkflowRuntimeInteractionRecord
{
    public Guid RequestId { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public Guid WorkflowRunId { get; set; }
    public Guid? NodeExecutionId { get; set; }
    public string InteractionType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? SignalName { get; set; }
    public string? CorrelationValue { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string RequestJson { get; set; } = "{}";
    public string DataJson { get; set; } = "{}";
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? AppliedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
