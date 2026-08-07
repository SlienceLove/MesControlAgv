namespace MesControlAgv.Mes.Entities;

public sealed class FieldNavigationAcceptance
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Status { get; set; } = string.Empty;
    public string AgvId { get; init; } = string.Empty;
    public string SourceStationId { get; init; } = string.Empty;
    public string TargetStationId { get; init; } = string.Empty;
    public string MapName { get; init; } = string.Empty;
    public string MapMd5 { get; init; } = string.Empty;
    public string PlannedPathJson { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? OperatorName { get; set; }
    public string? SafetyObserverName { get; set; }
    public string? PermitId { get; set; }
    public DateTimeOffset? AuthorizedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset? PermitConsumedAtUtc { get; set; }
    public string? DeviceTaskId { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FieldNavigationAcceptanceAudit
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid AcceptanceId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string DetailsJson { get; init; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
