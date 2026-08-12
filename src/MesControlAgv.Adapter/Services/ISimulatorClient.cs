using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Services;

public interface IAgvDeviceClient
{
    Task EnsureControlAsync(CancellationToken cancellationToken);
    Task<bool> ReleaseControlAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<AgvTaskResponse?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken);
    Task<AgvTaskResponse> NavigateAsync(Guid taskId, string? sourceStationId, string stationId, CancellationToken cancellationToken);
    Task<AgvTaskResponse?> PauseAsync(Guid taskId, CancellationToken cancellationToken);
    Task<AgvTaskResponse?> ResumeAsync(Guid taskId, CancellationToken cancellationToken);
    Task<AgvTaskResponse?> CancelAsync(Guid taskId, CancellationToken cancellationToken);

    Task<AgvTaskResponse?> GetTaskAsync(Guid taskId, IReadOnlyList<string>? path, CancellationToken cancellationToken) =>
        GetTaskAsync(taskId, cancellationToken);
    Task<AgvTaskResponse> NavigateAsync(Guid taskId, string? sourceStationId, string stationId, IReadOnlyList<string>? path, CancellationToken cancellationToken) =>
        NavigateAsync(taskId, sourceStationId, stationId, cancellationToken);
    Task<AgvTaskResponse?> PauseAsync(Guid taskId, IReadOnlyList<string>? path, CancellationToken cancellationToken) =>
        PauseAsync(taskId, cancellationToken);
    Task<AgvTaskResponse?> ResumeAsync(Guid taskId, IReadOnlyList<string>? path, CancellationToken cancellationToken) =>
        ResumeAsync(taskId, cancellationToken);
    Task<AgvTaskResponse?> CancelAsync(Guid taskId, IReadOnlyList<string>? path, CancellationToken cancellationToken) =>
        CancelAsync(taskId, cancellationToken);
}

/// <summary>
/// Optional physical-driver evidence that distinguishes a failure before any
/// navigation write from a write whose result may be unknown.
/// </summary>
public interface INavigationAttemptState
{
    bool MayHaveWrittenNavigation(Guid taskId);
}

/// <summary>
/// Optional physical-driver evidence that distinguishes a failure before any
/// cancellation write from a write whose result may be unknown.
/// </summary>
public interface ICancellationAttemptState
{
    bool MayHaveWrittenCancellation(Guid taskId);
}

/// <summary>
/// Optional evidence used by a physical dispatch session to release only the
/// control ownership that the same session acquired.
/// </summary>
public interface IControlAcquisitionEvidence
{
    Task<ControlAcquisitionResult> EnsureControlWithResultAsync(CancellationToken cancellationToken);
}

public readonly record struct ControlAcquisitionResult(bool AcquiredByThisCall);

public interface ISimulatorClient : IAgvDeviceClient
{
}

/// <summary>
/// Implemented by physical-device clients that support a read-only 1101-style
/// safety query. Simulator implementations intentionally do not expose it.
/// </summary>
public interface IPhysicalAgvDeviceClient
{
    Task<AgvSafetyReadinessResponse> GetSafetyReadinessAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Optional physical-driver capability for obtaining a complete, fresh and
/// controller-authoritative map catalog without sending a control command.
/// </summary>
public interface IControllerMapEvidenceDeviceClient
{
    Task<ControllerMapEvidenceResponse?> GetControllerMapEvidenceAsync(CancellationToken cancellationToken);
}

public interface IAgvFleetDeviceClient
{
    Task<IReadOnlyList<AgvSnapshotResponse>> GetFleetSnapshotAsync(CancellationToken cancellationToken);
    Task<AgvTaskResponse?> GetTaskAsync(string agvId, Guid taskId, CancellationToken cancellationToken);
    Task<AgvTaskResponse> NavigateAsync(
        string agvId,
        Guid taskId,
        string? sourceStationId,
        string stationId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken);
    Task<AgvTaskResponse?> PauseAsync(string agvId, Guid taskId, CancellationToken cancellationToken);
    Task<AgvTaskResponse?> ResumeAsync(string agvId, Guid taskId, CancellationToken cancellationToken);
    Task<AgvTaskResponse?> CancelAsync(string agvId, Guid taskId, CancellationToken cancellationToken);

    Task<AgvTaskResponse?> GetTaskAsync(string agvId, Guid taskId, IReadOnlyList<string>? path, CancellationToken cancellationToken) =>
        GetTaskAsync(agvId, taskId, cancellationToken);
    Task<AgvTaskResponse?> PauseAsync(string agvId, Guid taskId, IReadOnlyList<string>? path, CancellationToken cancellationToken) =>
        PauseAsync(agvId, taskId, cancellationToken);
    Task<AgvTaskResponse?> ResumeAsync(string agvId, Guid taskId, IReadOnlyList<string>? path, CancellationToken cancellationToken) =>
        ResumeAsync(agvId, taskId, cancellationToken);
    Task<AgvTaskResponse?> CancelAsync(string agvId, Guid taskId, IReadOnlyList<string>? path, CancellationToken cancellationToken) =>
        CancelAsync(agvId, taskId, cancellationToken);
}

public sealed class ControlUnavailableException(string owner)
    : InvalidOperationException($"AGV control owner is {owner}.");

public sealed class ControlReleaseUnconfirmedException()
    : InvalidOperationException("AGV control release was not confirmed by API 1060.");

public sealed class SingleAgvFleetDeviceClient(string agvId, IAgvDeviceClient device) : IAgvFleetDeviceClient
{
    public async Task<IReadOnlyList<AgvSnapshotResponse>> GetFleetSnapshotAsync(CancellationToken cancellationToken) =>
        [(await device.GetSnapshotAsync(cancellationToken)) with { AgvId = agvId }];

    public async Task<AgvTaskResponse?> GetTaskAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        (await device.GetTaskAsync(taskId, cancellationToken)) is { } task
            ? task with { AgvId = agvId }
            : null;

    public async Task<AgvTaskResponse> NavigateAsync(
        string agvId,
        Guid taskId,
        string? sourceStationId,
        string stationId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken) =>
        (await device.NavigateAsync(taskId, sourceStationId, stationId, path, cancellationToken))
            with { AgvId = agvId, Path = path };

    public async Task<AgvTaskResponse?> GetTaskAsync(string agvId, Guid taskId, IReadOnlyList<string>? path, CancellationToken cancellationToken) =>
        (await device.GetTaskAsync(taskId, path, cancellationToken)) is { } task
            ? task with { AgvId = agvId, Path = path }
            : null;

    public async Task<AgvTaskResponse?> PauseAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        (await device.PauseAsync(taskId, cancellationToken)) is { } task
            ? task with { AgvId = agvId }
            : null;

    public async Task<AgvTaskResponse?> ResumeAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        (await device.ResumeAsync(taskId, cancellationToken)) is { } task
            ? task with { AgvId = agvId }
            : null;

    public async Task<AgvTaskResponse?> CancelAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        (await device.CancelAsync(taskId, cancellationToken)) is { } task
            ? task with { AgvId = agvId }
            : null;

    public async Task<AgvTaskResponse?> PauseAsync(string agvId, Guid taskId, IReadOnlyList<string>? path, CancellationToken cancellationToken) =>
        (await device.PauseAsync(taskId, path, cancellationToken)) is { } task
            ? task with { AgvId = agvId, Path = path }
            : null;

    public async Task<AgvTaskResponse?> ResumeAsync(string agvId, Guid taskId, IReadOnlyList<string>? path, CancellationToken cancellationToken) =>
        (await device.ResumeAsync(taskId, path, cancellationToken)) is { } task
            ? task with { AgvId = agvId, Path = path }
            : null;

    public async Task<AgvTaskResponse?> CancelAsync(string agvId, Guid taskId, IReadOnlyList<string>? path, CancellationToken cancellationToken) =>
        (await device.CancelAsync(taskId, path, cancellationToken)) is { } task
            ? task with { AgvId = agvId, Path = path }
            : null;
}
