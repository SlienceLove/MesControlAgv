namespace MesControlAgv.Mes.Entities;

public sealed class WorkflowExecutionRecord
{
    public Guid RequestId { get; set; }

    public string Fingerprint { get; set; } = string.Empty;

    public Guid WorkflowId { get; set; }

    public int Version { get; set; }

    public Guid ExecutionId { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string? RejectionCode { get; set; }

    public string RequestJson { get; set; } = "{}";

    public string ResultJson { get; set; } = "{}";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
