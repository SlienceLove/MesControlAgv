using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public sealed class TestAdapterClient : IAdapterClient
{
    public Task<AdapterTask> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken) =>
        Task.FromResult(new AdapterTask(operationId, operationId.ToString("N"), targetStationId, "moving", null));

    public Task<AdapterTask?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult<AdapterTask?>(null);

    public Task<AdapterTask?> CancelAsync(Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult<AdapterTask?>(null);

    public Task<AdapterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AdapterSnapshot(true, "adapter", "CHARGE_01", null));
}
