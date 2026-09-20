namespace MesControlAgv.Mes.Entities;

public sealed class ExperimentSampleVerificationRecord
{
    public Guid VerificationId { get; set; }
    public Guid ExperimentJobId { get; set; }
    public int Revision { get; set; }
    public string Status { get; set; } = string.Empty;
    public string RowsJson { get; set; } = "[]";
    public string SnapshotHash { get; set; } = string.Empty;
    public string? VerifiedBy { get; set; }
    public DateTime? VerifiedAtUtc { get; set; }
    public string? VerificationNote { get; set; }
    public DateTime? InvalidatedAtUtc { get; set; }
    public string? InvalidationReason { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
