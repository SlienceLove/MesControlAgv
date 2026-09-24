namespace MesControlAgv.Contracts;

public sealed record WorkstationBatchRequest(Guid RequestId, string Actor,
    IReadOnlyList<SampleWorkstationTaskTemplate> Tasks);
public sealed record WorkstationBatchItem(string TaskNo, string TaskName, string Status, string? Message);
public sealed record WorkstationBatchResult(Guid RequestId, string DeviceId, string PayloadHash,
    IReadOnlyList<WorkstationBatchItem> Items, DateTimeOffset UpdatedAtUtc, string? Actor = null)
{
    public SampleWorkstationTemplateVolumeUnit? FileVolumeUnit { get; init; }
}
