using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Mes.Tests;

public sealed class TestAdapterClient : IAgvGateway
{
    public Task<AgvTaskResponse> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken) =>
        Task.FromResult(new AgvTaskResponse(operationId, operationId.ToString("N"), targetStationId, "moving", null));

    public Task<AgvTaskResponse?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult<AgvTaskResponse?>(null);

    public Task<AgvTaskResponse?> CancelAsync(Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult<AgvTaskResponse?>(null);

    public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AgvSnapshotResponse(true, "adapter", "CHARGE_01", null));

    public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(
        string agvId,
        string command,
        Guid? taskId,
        CancellationToken cancellationToken) =>
        Task.FromResult<AgvTaskResponse?>(null);
}


