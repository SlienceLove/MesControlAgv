namespace MesControlAgv.Mes.Entities;

public sealed class ScheduleEntryRecord
{
    public Guid ScheduleEntryId { get; set; }
    public Guid ExperimentJobId { get; set; }
    public DateTime PlannedStartUtc { get; set; }
    public DateTime PlannedEndUtc { get; set; }
    public int Priority { get; set; }
    public string Status { get; set; } = string.Empty;
    public string RequestedResourcesJson { get; set; } = "[]";
    public string BlockingReasonsJson { get; set; } = "[]";
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
