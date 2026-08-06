namespace MesControlAgv.Mes.Entities;

public sealed class WorkflowAuditRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string EventType { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string? Code { get; set; }

    public string? Reason { get; set; }

    public Guid WorkflowId { get; set; }

    public int Version { get; set; }

    public Guid? RequestId { get; set; }

    public Guid? ExecutionId { get; set; }

    public string? Actor { get; set; }

    public string? CorrelationId { get; set; }

    public string DetailsJson { get; set; } = "{}";

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}
