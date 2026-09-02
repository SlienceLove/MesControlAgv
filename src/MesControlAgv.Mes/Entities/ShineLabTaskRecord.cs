namespace MesControlAgv.Mes.Entities;

public sealed class ShineLabTaskRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TaskUuid { get; set; } = string.Empty;
    public string EquipmentCode { get; set; } = string.Empty;
    public string Status { get; set; } = "Created";
    public string CurrentStage { get; set; } = "Created";
    public string RequestFingerprint { get; set; } = string.Empty;
    public string ConfigJson { get; set; } = string.Empty;
    public string? ConfigResponseJson { get; set; }
    public string? CommandResponseJson { get; set; }
    public string? ResultJson { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class ShineLabTaskEventRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TaskUuid { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
