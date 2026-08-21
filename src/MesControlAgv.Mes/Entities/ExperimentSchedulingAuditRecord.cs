namespace MesControlAgv.Mes.Entities;

public sealed class ExperimentSchedulingAuditRecord
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string? Code { get; set; }
    public Guid RequestId { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public Guid? PlanId { get; set; }
    public int? PlanVersion { get; set; }
    public Guid? ExperimentJobId { get; set; }
    public Guid? ScheduleEntryId { get; set; }
    public string DetailsJson { get; set; } = "{}";
    public string ResultJson { get; set; } = "{}";
    public DateTime OccurredAtUtc { get; set; }
}
