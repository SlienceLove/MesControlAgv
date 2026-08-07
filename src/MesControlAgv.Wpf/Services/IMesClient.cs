using MesControlAgv.Contracts;

﻿namespace MesControlAgv.Wpf.Services;

public sealed record DashboardTask(
    Guid Id,
    int SourceStationCode,
    int TargetStationCode,
    string Status,
    int RetryCount,
    string? LastError,
    int Priority = 0,
    string? Description = null,
    string? ExternalId = null,
    DateTime CreatedAt = default,
    DateTime? EndedAt = null,
    string? ActiveAgvId = null,
    string? ActiveDeviceTaskId = null,
    IReadOnlyList<string>? ActivePath = null);

public sealed record AgvDashboardSnapshot(
    bool Online,
    string ControlOwner,
    string? CurrentStationId,
    Guid? CurrentTaskId,
    string AgvId = "AGV-01",
    AgvCapabilitiesResponse? Capabilities = null);

public sealed record AgvActiveTaskStatus(
    Guid TransportTaskId,
    Guid OperationId,
    string MesStatus,
    string? DeviceTaskId,
    string? DeviceState,
    string? TargetStationId,
    string? LastError,
    IReadOnlyList<string>? Path);

public sealed record AgvFleetDashboardStatus(
    AgvDashboardSnapshot Snapshot,
    AgvActiveTaskStatus? ActiveTask);

public sealed record AgvCommandResult(Guid TaskId, string DeviceTaskId, string TargetStationId, string State, string? LastError, string AgvId = "AGV-01", IReadOnlyList<string>? Path = null);
public sealed record DashboardTaskEvent(Guid Id, string EventType, string Payload, DateTime CreatedAt);
public sealed record DashboardTaskDetail(DashboardTask Task, IReadOnlyList<DashboardTaskEvent> Events);
public sealed record DashboardStation(int Code, string Name, string AgvStationId, bool Enabled);
public sealed record DashboardPlannedPath(
    IReadOnlyList<string> Stations,
    double Cost,
    string? SourceStationId = null,
    string? TargetStationId = null);

public interface IMesClient
{
    Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DashboardTask>> GetTasksAsync(DateOnly date, CancellationToken cancellationToken) => GetTasksAsync(cancellationToken);
    Task<KpiDashboard> GetKpiDashboardAsync(DateOnly date, CancellationToken cancellationToken);
    Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DashboardStation>> GetStationsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DashboardStation>>([]);
    Task<DashboardPlannedPath> PlanPathAsync(
        string fromStationId,
        string toStationId,
        IReadOnlyCollection<string>? blockedStations,
        CancellationToken cancellationToken) =>
        Task.FromException<DashboardPlannedPath>(new NotSupportedException("Path planning is not supported by this MES client."));
    Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken);
    async Task<IReadOnlyList<AgvDashboardSnapshot>> GetAgvFleetAsync(CancellationToken cancellationToken) => [await GetAgvSnapshotAsync(cancellationToken)];
    async Task<IReadOnlyList<AgvFleetDashboardStatus>> GetAgvFleetStatusAsync(CancellationToken cancellationToken) =>
        (await GetAgvFleetAsync(cancellationToken))
            .Select(snapshot => new AgvFleetDashboardStatus(snapshot, null))
            .ToList();
    Task<AgvCommandResult?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken) => Task.FromResult<AgvCommandResult?>(null);
    Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken);
    Task<DashboardTask> CreateTaskAsync(int sourceStationCode, int targetStationCode, int priority, string? description, string? externalId, CancellationToken cancellationToken);
    Task<DashboardTask> DispatchTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromException<DashboardTask>(new NotSupportedException("Task dispatch is not supported by this MES client."));
    Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken);
    Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken);
    Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken);
    Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken);
    Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken);
    Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken);
}
