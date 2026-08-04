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

public interface IAgvFleetDeviceClient
{
    Task<IReadOnlyList<AgvSnapshotResponse>> GetFleetSnapshotAsync(CancellationToken cancellationToken);
    Task<AdapterTaskResponse?> GetTaskAsync(string agvId, Guid taskId, CancellationToken cancellationToken);
    Task<AdapterTaskResponse> NavigateAsync(
        string agvId,
        Guid taskId,
        string? sourceStationId,
        string stationId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken);
    Task<AdapterTaskResponse?> PauseAsync(string agvId, Guid taskId, CancellationToken cancellationToken);
    Task<AdapterTaskResponse?> ResumeAsync(string agvId, Guid taskId, CancellationToken cancellationToken);
    Task<AdapterTaskResponse?> CancelAsync(string agvId, Guid taskId, CancellationToken cancellationToken);
}

public sealed class ControlUnavailableException(string owner)
    : InvalidOperationException($"AGV control owner is {owner}.");

public sealed class SingleAgvFleetDeviceClient(string agvId, IAgvDeviceClient device) : IAgvFleetDeviceClient
{
    public async Task<IReadOnlyList<AgvSnapshotResponse>> GetFleetSnapshotAsync(CancellationToken cancellationToken) =>
        [(await device.GetSnapshotAsync(cancellationToken)) with { AgvId = agvId }];

    public async Task<AdapterTaskResponse?> GetTaskAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        (await device.GetTaskAsync(taskId, cancellationToken)) is { } task
            ? task with { AgvId = agvId }
            : null;

    public async Task<AdapterTaskResponse> NavigateAsync(
        string agvId,
        Guid taskId,
        string? sourceStationId,
        string stationId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken) =>
        (await device.NavigateAsync(taskId, sourceStationId, stationId, cancellationToken))
            with { AgvId = agvId, Path = path };

    public async Task<AdapterTaskResponse?> PauseAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        (await device.PauseAsync(taskId, cancellationToken)) is { } task
            ? task with { AgvId = agvId }
            : null;

    public async Task<AdapterTaskResponse?> ResumeAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        (await device.ResumeAsync(taskId, cancellationToken)) is { } task
            ? task with { AgvId = agvId }
            : null;

    public async Task<AdapterTaskResponse?> CancelAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        (await device.CancelAsync(taskId, cancellationToken)) is { } task
            ? task with { AgvId = agvId }
            : null;
}
