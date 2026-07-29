using MesControlAgv.Adapter.Contracts;

namespace MesControlAgv.Adapter.Services;

public interface ISimulatorClient
{
    Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<AdapterTaskResponse?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken);
    Task<AdapterTaskResponse> NavigateAsync(Guid taskId, string stationId, CancellationToken cancellationToken);
}

public sealed class ControlUnavailableException(string owner)
    : InvalidOperationException($"AGV control owner is {owner}.");
