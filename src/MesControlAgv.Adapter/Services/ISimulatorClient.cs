using MesControlAgv.Adapter.Contracts;

namespace MesControlAgv.Adapter.Services;

public interface IAgvDeviceClient
{
    Task EnsureControlAsync(CancellationToken cancellationToken);
    Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<AdapterTaskResponse?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken);
    Task<AdapterTaskResponse> NavigateAsync(Guid taskId, string? sourceStationId, string stationId, CancellationToken cancellationToken);
    Task<AdapterTaskResponse?> PauseAsync(Guid taskId, CancellationToken cancellationToken);
    Task<AdapterTaskResponse?> ResumeAsync(Guid taskId, CancellationToken cancellationToken);
    Task<AdapterTaskResponse?> CancelAsync(Guid taskId, CancellationToken cancellationToken);
}

public interface ISimulatorClient : IAgvDeviceClient
{
}

public sealed class ControlUnavailableException(string owner)
    : InvalidOperationException($"AGV control owner is {owner}.");
