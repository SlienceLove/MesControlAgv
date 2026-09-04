namespace MesControlAgv.Mes.Entities;

/// <summary>
/// Durable outer runtime snapshot for an experiment job. The step collection is
/// serialized as one immutable-shaped snapshot so a service restart can rebuild
/// the device-free composite state machine before child workflow orchestration
/// is enabled.
/// </summary>
public sealed class ExperimentRunRecord
{
    public Guid ExperimentRunId { get; set; }
    public Guid ExperimentJobId { get; set; }
    public Guid PlanId { get; set; }
    public int PlanVersion { get; set; }
    public Guid AdmissionRequestId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int CurrentStepOrder { get; set; }
    public Guid? CurrentStepRunId { get; set; }
    public string StepsJson { get; set; } = "[]";
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
