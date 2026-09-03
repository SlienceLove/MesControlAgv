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
/// Optional, read-only identity proof returned by an Adapter before a
/// Simulator-only worker is allowed to issue a dispatch request.
/// </summary>
public interface IAdapterRuntimeIdentityGateway
{
    Task<AdapterRuntimeIdentityResponse> GetRuntimeIdentityAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Optional port implemented only by drivers that can produce a read-only
/// physical acceptance assessment.
/// </summary>
public interface IPhysicalPreflightAgvGateway
{
    Task<PhysicalAgvPreflightResponse> GetPhysicalPreflightAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Optional physical-session cleanup capability. Implementations release only
/// control currently owned by the Adapter and must never acquire control as a
/// side effect of cleanup.
/// </summary>
public interface IPhysicalAgvControlGateway
{
    Task<bool> ReleaseControlAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Optional AGV digital-I/O gateway used by station handshakes. Navigation and
/// I/O remain separate capabilities so a simulator or a legacy adapter cannot
/// accidentally claim to drive physical outputs.
/// </summary>
public interface IAgvIoGateway
{
    Task<AgvIoSnapshotResponse> GetIoAsync(
        string agvId,
        CancellationToken cancellationToken);

    Task<AgvDoWriteResponse> SetDoAsync(
        string agvId,
        int id,
        bool status,
        CancellationToken cancellationToken);
}
