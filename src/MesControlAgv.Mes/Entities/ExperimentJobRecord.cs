namespace MesControlAgv.Mes.Entities;

public sealed class ExperimentJobRecord
{
    public Guid JobId { get; set; }
    public Guid PlanId { get; set; }
    public int PlanVersion { get; set; }
    public Guid WorkflowId { get; set; }
    public int WorkflowVersion { get; set; }
    public string SampleBatchId { get; set; } = string.Empty;
    public string? SampleId { get; set; }
    public string ParametersJson { get; set; } = "{}";
    public string Status { get; set; } = string.Empty;
    public Guid? WorkflowRunId { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? LastError { get; set; }
}
