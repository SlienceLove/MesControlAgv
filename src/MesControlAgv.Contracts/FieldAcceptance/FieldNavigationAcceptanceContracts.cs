namespace MesControlAgv.Contracts;

public sealed record CreateFieldNavigationAcceptanceRequest(
    string AgvId,
    string SourceStationId,
    string TargetStationId,
    string? Description = null);

public sealed record AuthorizeFieldNavigationAcceptanceRequest(
    string OperatorName,
    string SafetyObserverName,
    string PermitId,
    DateTimeOffset ExpiresAtUtc);

public sealed record FieldNavigationDispatchCommand(
    string AgvId,
    string SourceStationId,
    string TargetStationId,
    IReadOnlyList<string> PlannedPath);

public sealed record FieldNavigationAcceptanceResponse(
    Guid Id,
    string Status,
    string AgvId,
    string SourceStationId,
    string TargetStationId,
    string MapName,
    string MapMd5,
    IReadOnlyList<string> PlannedPath,
    string? Description,
    string? OperatorName,
    string? SafetyObserverName,
    string? PermitId,
    DateTimeOffset? AuthorizedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? PermitConsumedAtUtc,
    string? DeviceTaskId,
    string? LastError,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record FieldNavigationAcceptanceAuditResponse(
    Guid Id,
    string EventType,
    string Details,
    DateTimeOffset OccurredAtUtc);

public sealed record FieldNavigationAcceptanceDetailResponse(
    FieldNavigationAcceptanceResponse Acceptance,
    IReadOnlyList<FieldNavigationAcceptanceAuditResponse> Audits);

public static class FieldNavigationAcceptanceStatuses
{
    public const string Draft = "draft";
    public const string Authorized = "authorized";
    public const string Dispatching = "dispatching";
    public const string Accepted = "accepted";
    public const string Moving = "moving";
    public const string Arrived = "arrived";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
}
