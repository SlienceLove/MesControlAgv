namespace MesControlAgv.Contracts;

public sealed record WorkstationBatchRequest(Guid RequestId, string Actor,
    IReadOnlyList<SampleWorkstationTaskTemplate> Tasks);
public sealed record WorkstationBatchItem(string TaskNo, string TaskName, string Status, string? Message);
public sealed record WorkstationBatchResult(Guid RequestId, string DeviceId, string PayloadHash,
    IReadOnlyList<WorkstationBatchItem> Items, DateTimeOffset UpdatedAtUtc, string? Actor = null)
{
    public SampleWorkstationTemplateVolumeUnit? FileVolumeUnit { get; init; }
    public IReadOnlyList<WorkstationBatchReconciliation>? Reconciliations { get; init; }
}

public sealed record WorkstationBatchReconcileRequest(Guid ConfirmationId, string TaskNo,
    string ExpectedPayloadHash, string ExpectedJournalSha256, string Actor, string Reason,
    bool ConfirmTaskCreated, bool ConfirmTransfersChecked);

public sealed record WorkstationBatchReconciliation(Guid RequestId, string DeviceId,
    string TaskNo, string TaskName, string OriginalStatus, string? OriginalMessage,
    string PayloadHash, string JournalSha256, WorkstationBatchReconcileRequest Confirmation,
    SampleWorkstationTaskDetailsResponse DeviceTask, SampleWorkstationStatusResponse DeviceStatus,
    DateTimeOffset ConfirmedAtUtc, string Outcome);

public sealed record WorkstationBatchReviewItem(Guid RequestId, string DeviceId,
    string TaskNo, string TaskName, string OriginalStatus, string? OriginalMessage,
    string PayloadHash, string JournalSha256, WorkstationBatchReconciliation? Reconciliation);
