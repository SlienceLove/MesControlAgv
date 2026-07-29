namespace MesControlAgv.Mes.Contracts;

public sealed record CreateTaskRequest(int SourceStationCode, int TargetStationCode);

public sealed record TaskResponse(
    Guid Id,
    int SourceStationCode,
    int TargetStationCode,
    string Status,
    int RetryCount,
    string? LastError);

public sealed record TaskEventResponse(
    Guid Id,
    string EventType,
    string Payload,
    DateTime CreatedAt);

public sealed record TaskDetailResponse(TaskResponse Task, IReadOnlyList<TaskEventResponse> Events);

public sealed record StationResponse(int Code, string Name, string AgvStationId, bool Enabled);

public sealed record OperatorActionRequest(string OperatorName);
