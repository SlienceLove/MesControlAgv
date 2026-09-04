namespace MesControlAgv.Mes.Entities;

public sealed class ExperimentPlanRecord
{
    public Guid PlanId { get; set; }
    public int Version { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid WorkflowId { get; set; }
    public int WorkflowVersion { get; set; }
    public string WorkflowStepsJson { get; set; } = "[]";
    public string Status { get; set; } = string.Empty;
    public string MaterialRequirementsJson { get; set; } = "[]";
    public string DefaultParametersJson { get; set; } = "{}";
    public string ResourceRequirementsJson { get; set; } = "[]";
    public string? ProfileProductId { get; set; }
    public string? ProfileVersion { get; set; }
    public string? LayoutId { get; set; }
    public string? ValidationJson { get; set; }
    public string? ValidatedBy { get; set; }
    public DateTime? ValidatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string? PublishedBy { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
