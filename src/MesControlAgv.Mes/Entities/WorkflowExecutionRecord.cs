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

    /// <summary>Immutable serialized workflow definition pinned at admission.</summary>
    public string? DefinitionSnapshotJson { get; set; }

    /// <summary>Durable orchestration state; device dispatch remains a later phase.</summary>
    public string? RuntimeStatus { get; set; }

    public Guid? CurrentNodeId { get; set; }

    public string? PendingStepJson { get; set; }

    public Guid? TransportOperationId { get; set; }

    public int Attempt { get; set; }

    public string? LastError { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
}
