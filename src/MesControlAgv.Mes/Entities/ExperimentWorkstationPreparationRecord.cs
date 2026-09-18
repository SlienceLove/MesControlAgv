namespace MesControlAgv.Mes.Entities;

public sealed class ExperimentWorkstationPreparationRecord
{
    public Guid PreparationId { get; set; }
    public Guid ExperimentJobId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string VendorTaskNo { get; set; } = string.Empty;
    public Guid VerificationId { get; set; }
    public int VerificationRevision { get; set; }
    public string VerificationSnapshotHash { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string PayloadHash { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Guid PreparedRequestId { get; set; }
    public Guid? ImportRequestId { get; set; }
    public DateTime PreparedAtUtc { get; set; }
    public DateTime? ImportingAtUtc { get; set; }
    public DateTime? ImportedAtUtc { get; set; }
    public DateTime? UnknownAtUtc { get; set; }
    public string? LastError { get; set; }
}
