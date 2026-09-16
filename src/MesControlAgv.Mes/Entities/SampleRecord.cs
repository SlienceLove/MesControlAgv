namespace MesControlAgv.Mes.Entities;

public sealed class SampleRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SampleId { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;
    public string SampleBatchId { get; set; } = string.Empty;
    public string SourceLocation { get; set; } = string.Empty;
    public string? ContainerPosition { get; set; }
    public string? CurrentLocation { get; set; }
    public string Status { get; set; } = "Registered";
    public Guid? RunId { get; set; }
    public string? LastDeviceId { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? LastError { get; set; }
}

public sealed class SampleEventRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SampleRecordId { get; set; }
    public Guid? OperationId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string? FromLocation { get; set; }
    public string? ToLocation { get; set; }
    public string Actor { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? Detail { get; set; }
}
