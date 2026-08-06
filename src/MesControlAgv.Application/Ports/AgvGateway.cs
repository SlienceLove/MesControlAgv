using MesControlAgv.Contracts;

namespace MesControlAgv.Application;

/// <summary>
/// Application port for normalized AGV operations. The concrete HTTP/TCP implementation belongs to infrastructure.
/// </summary>
public interface IAgvGateway
{
    Task<AgvTaskResponse> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken);
    Task<AgvTaskResponse?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken);
    Task<AgvTaskResponse?> CancelAsync(Guid operationId, CancellationToken cancellationToken);
    Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<AgvTaskResponse?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken);
}

public interface IRouteAwareAgvGateway
{
    Task<AgvTaskResponse> DispatchAsync(
        Guid operationId,
        string sourceStationId,
        string targetStationId,
        CancellationToken cancellationToken);
}

public interface IPathAwareAgvGateway : IRouteAwareAgvGateway
{
    Task<AgvTaskResponse> DispatchAsync(
        Guid operationId,
        string sourceStationId,
        string targetStationId,
        IReadOnlyList<string> plannedPath,
        CancellationToken cancellationToken);
}

public interface IFleetAwareAgvGateway
{
    Task<IReadOnlyList<AgvSnapshotResponse>> GetFleetSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Optional port implemented only by drivers that can produce a read-only
/// physical acceptance assessment.
/// </summary>
public interface IPhysicalPreflightAgvGateway
{
    Task<PhysicalAgvPreflightResponse> GetPhysicalPreflightAsync(CancellationToken cancellationToken);
}
