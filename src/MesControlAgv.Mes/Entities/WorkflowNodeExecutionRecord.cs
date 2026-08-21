namespace MesControlAgv.Mes.Entities;

public sealed class WorkflowNodeExecutionRecord
{
    public Guid Id { get; set; }

    public Guid WorkflowRunId { get; set; }

    public Guid WorkflowId { get; set; }

    public int Version { get; set; }

    public Guid StepRequestId { get; set; }

    public Guid NodeId { get; set; }

    public string NodeTypeId { get; set; } = string.Empty;

    public string NodeName { get; set; } = string.Empty;

    public int Attempt { get; set; }

    public string Status { get; set; } = string.Empty;

    public string InputJson { get; set; } = "{}";

    public string OutputJson { get; set; } = "{}";

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public string? LastError { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
