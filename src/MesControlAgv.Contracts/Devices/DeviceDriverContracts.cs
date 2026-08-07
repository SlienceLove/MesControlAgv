namespace MesControlAgv.Contracts;

/// <summary>
/// Normalized navigation command exposed by an AGV driver.
/// </summary>
public sealed record AgvDispatchCommand(
    Guid TaskId,
    string AgvId,
    string TargetStationId,
    string? SourceStationId = null,
    IReadOnlyList<string>? Path = null);

/// <summary>
/// Normalized task-control command exposed by an AGV driver.
/// </summary>
public sealed record AgvControlCommand(
    Guid TaskId,
    string AgvId);
