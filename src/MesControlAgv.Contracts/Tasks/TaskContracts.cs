namespace MesControlAgv.Contracts;

public sealed record CreateTaskRequest(
    int SourceStationCode,
    int TargetStationCode,
    int Priority = 0,
    string? Description = null,
    string? ExternalId = null);

public sealed record TaskResponse(
    Guid Id,
    int SourceStationCode,
    int TargetStationCode,
    string Status,
    int RetryCount,
    string? LastError,
    int Priority = 0,
    string? Description = null,
    string? ExternalId = null,
    DateTime CreatedAt = default,
    DateTime? EndedAt = null,
    string? ActiveAgvId = null,
    string? ActiveDeviceTaskId = null,
    IReadOnlyList<string>? ActivePath = null);

public sealed record TaskEventResponse(
    Guid Id,
    string EventType,
    string Payload,
    DateTime CreatedAt);

public sealed record TaskDetailResponse(TaskResponse Task, IReadOnlyList<TaskEventResponse> Events);

public sealed record StationResponse(int Code, string Name, string AgvStationId, bool Enabled);

public sealed record OperatorActionRequest(string OperatorName);

public sealed record PlanPathRequest(
    string FromStationId,
    string ToStationId,
    IReadOnlyCollection<string>? BlockedStations = null);

public sealed record PlannedPathResponse(IReadOnlyList<string> Stations, double Cost);
