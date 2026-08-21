namespace MesControlAgv.Mes.Entities;

public sealed class WorkflowResourceLeaseRecord
{
    public Guid LeaseId { get; set; }
    public Guid? ScheduleEntryId { get; set; }
    public Guid WorkflowRunId { get; set; }
    public Guid? NodeExecutionId { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceKey { get; set; } = string.Empty;

    /// <summary>
    /// Equal to ResourceKey while active and null after explicit release. The
    /// unique index on this column is the final database-level mutex.
    /// </summary>
    public string? ActiveResourceKey { get; set; }

    public string Status { get; set; } = string.Empty;
    public string AcquiredBy { get; set; } = string.Empty;
    public DateTime AcquiredAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string? ReleasedBy { get; set; }
    public string? ReleaseReason { get; set; }
    public DateTime? ReleasedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
