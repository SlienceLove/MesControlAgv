namespace MesControlAgv.Wpf.Services;

public sealed record DashboardTask(
    Guid Id,
    int SourceStationCode,
    int TargetStationCode,
    string Status,
    int RetryCount,
    string? LastError,
    int Priority = 0,
    string? Description = null,
    string? ExternalId = null);

public sealed record AgvDashboardSnapshot(
    bool Online,
    string ControlOwner,
    string? CurrentStationId,
    Guid? CurrentTaskId,
    string AgvId = "AGV-01");

public sealed record AgvCommandResult(Guid TaskId, string DeviceTaskId, string TargetStationId, string State, string? LastError, string AgvId = "AGV-01", IReadOnlyList<string>? Path = null);
public sealed record DashboardTaskEvent(Guid Id, string EventType, string Payload, DateTime CreatedAt);
public sealed record DashboardTaskDetail(DashboardTask Task, IReadOnlyList<DashboardTaskEvent> Events);

public interface IMesClient
{
    Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken);
    Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken);
    Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken);
    async Task<IReadOnlyList<AgvDashboardSnapshot>> GetAgvFleetAsync(CancellationToken cancellationToken) => [await GetAgvSnapshotAsync(cancellationToken)];
    Task<AgvCommandResult?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken) => Task.FromResult<AgvCommandResult?>(null);
    Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken);
    Task<DashboardTask> CreateTaskAsync(int sourceStationCode, int targetStationCode, int priority, string? description, string? externalId, CancellationToken cancellationToken) => CreateTaskAsync(cancellationToken);
    Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken);
    Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken);
    Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken);
    Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken);
    Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken);
    Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken);
}
