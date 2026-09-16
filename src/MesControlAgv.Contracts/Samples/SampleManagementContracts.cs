using System.Text.Json.Serialization;

namespace MesControlAgv.Contracts.Samples;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SampleLifecycleStatus
{
    Registered,
    Reserved,
    InTransit,
    AtWorkstation,
    Processing,
    Completed,
    Quarantined,
    Unknown
}

public sealed record RegisterSampleRequest
{
    public string SampleId { get; init; } = string.Empty;
    public string Barcode { get; init; } = string.Empty;
    public string SampleBatchId { get; init; } = string.Empty;
    public string SourceLocation { get; init; } = string.Empty;
    public string? ContainerPosition { get; init; }
    public string OperatorName { get; init; } = string.Empty;
}

public sealed record BindSampleRunRequest
{
    public Guid RunId { get; init; }
    public string OperatorName { get; init; } = string.Empty;
}

public sealed record MoveSampleRequest
{
    public Guid OperationId { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string ToLocation { get; init; } = string.Empty;
    public string OperatorName { get; init; } = string.Empty;
    public SampleLifecycleStatus? Status { get; init; }
}

/// <summary>
/// One row from the operator-maintained sample import template.  RunId is
/// optional so a warehouse can preload samples before a workflow is created.
/// </summary>
public sealed record SampleImportRowRequest
{
    public int RowNumber { get; init; }
    public string SampleId { get; init; } = string.Empty;
    public string Barcode { get; init; } = string.Empty;
    public string SampleBatchId { get; init; } = string.Empty;
    public string SourceLocation { get; init; } = string.Empty;
    public string? ContainerPosition { get; init; }
    public Guid? RunId { get; init; }
}

public sealed record ImportSamplesRequest
{
    public string SourceFileName { get; init; } = string.Empty;
    public string OperatorName { get; init; } = string.Empty;
    public IReadOnlyList<SampleImportRowRequest> Rows { get; init; } = [];
}

public sealed record SampleImportRowResult(
    int RowNumber,
    string SampleId,
    string? Barcode,
    string Outcome,
    string? Error,
    SampleRecordResponse? Sample);

public sealed record ImportSamplesResponse(
    string SourceFileName,
    int TotalRows,
    int SucceededRows,
    int ExistingRows,
    int FailedRows,
    IReadOnlyList<SampleImportRowResult> Rows);

public sealed record SampleRecordResponse(
    Guid Id,
    string SampleId,
    string Barcode,
    string SampleBatchId,
    string SourceLocation,
    string? ContainerPosition,
    string? CurrentLocation,
    SampleLifecycleStatus Status,
    Guid? RunId,
    string? LastDeviceId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? LastError);

public sealed record SampleEventResponse(
    Guid Id,
    Guid SampleRecordId,
    Guid? OperationId,
    string EventType,
    string DeviceId,
    string? FromLocation,
    string? ToLocation,
    string Actor,
    DateTimeOffset OccurredAtUtc,
    string? Detail);
