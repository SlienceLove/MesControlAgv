using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Mes.Tests;

public sealed class TestAdapterClient : IAgvGateway, IFleetAwareAgvGateway
{
    private Guid? _currentTaskId;

    public Task<AgvTaskResponse> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken) =>
        DispatchCoreAsync(operationId, targetStationId);

    private Task<AgvTaskResponse> DispatchCoreAsync(Guid operationId, string targetStationId)
    {
        _currentTaskId = operationId;
        return Task.FromResult(new AgvTaskResponse(operationId, operationId.ToString("N"), targetStationId, "moving", null));
    }

    public Task<AgvTaskResponse?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult(ReturnMissingTaskOnQuery
            ? null
            : new AgvTaskResponse(
                operationId,
                operationId.ToString("N"),
                "SAMPLE_01",
                "moving",
                null));

    public Task<AgvTaskResponse?> CancelAsync(Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult<AgvTaskResponse?>(null);

    public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AgvSnapshotResponse(true, "adapter", "CHARGE_01", _currentTaskId));

    public Task<IReadOnlyList<AgvSnapshotResponse>> GetFleetSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgvSnapshotResponse>>(
        [new AgvSnapshotResponse(true, "adapter", "CHARGE_01", _currentTaskId)]);

    public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(
        string agvId,
        string command,
        Guid? taskId,
        CancellationToken cancellationToken)
    {
        ExecuteAgvCommandCallCount++;
        return Task.FromResult<AgvTaskResponse?>(null);
    }

    public int ExecuteAgvCommandCallCount { get; private set; }
    public bool ReturnMissingTaskOnQuery { get; set; }
}


