namespace MesControlAgv.Mes.Entities;

public sealed class WorkflowVersionRecord
{
    public Guid WorkflowId { get; set; }

    public int Version { get; set; }

    public string DefinitionJson { get; set; } = "{}";

    public string Status { get; set; } = "Draft";

    public string PublishStatus { get; set; } = "NotPublished";

    public string? ValidationJson { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string? ChangeSummary { get; set; }

    public string? PublishedBy { get; set; }

    public DateTime? PublishedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
