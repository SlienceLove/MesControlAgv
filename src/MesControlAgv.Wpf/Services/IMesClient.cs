namespace MesControlAgv.Wpf.Services;

public sealed record DashboardTask(Guid Id, int SourceStationCode, int TargetStationCode, string Status, int RetryCount, string? LastError);
public sealed record AgvDashboardSnapshot(bool Online, string ControlOwner, string? CurrentStationId, Guid? CurrentTaskId);

public interface IMesClient
{
    Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken);
    Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken);
    Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken);
    Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken);
    Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken);
    Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken);
    Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken);
    Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken);
}
