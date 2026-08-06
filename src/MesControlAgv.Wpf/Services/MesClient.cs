using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using MesControlAgv.Contracts;
using ContractAgvSnapshot = MesControlAgv.Contracts.AgvSnapshotResponse;
using ContractAgvTask = MesControlAgv.Contracts.AgvTaskResponse;
using ContractKpiDashboard = MesControlAgv.Contracts.KpiDashboardResponse;
using ContractPlannedPath = MesControlAgv.Contracts.PlannedPathResponse;
using ContractTaskDetail = MesControlAgv.Contracts.TaskDetailResponse;
using ContractTaskResponse = MesControlAgv.Contracts.TaskResponse;

namespace MesControlAgv.Wpf.Services;

public sealed class MesClient(HttpClient client) : IMesClient
{
    public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken) =>
        GetTasksAsync(DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken);

    public async Task<IReadOnlyList<DashboardTask>> GetTasksAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var tasks = await client.GetFromJsonAsync<List<ContractTaskResponse>>(
            $"api/tasks?date={date:yyyy-MM-dd}", cancellationToken) ?? [];
        return tasks.Select(ToDashboardTask).ToList();
    }

    public async Task<KpiDashboard> GetKpiDashboardAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var dashboard = await client.GetFromJsonAsync<ContractKpiDashboard>(
            $"api/dashboard/kpi?date={date:yyyy-MM-dd}", cancellationToken)
            ?? throw new InvalidOperationException("MES returned no KPI dashboard.");
        return ToKpiDashboard(dashboard);
    }

    public async Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"api/tasks/{taskId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<ContractTaskDetail>(cancellationToken)
            ?? throw new InvalidOperationException("MES returned no task detail.");
        return new DashboardTaskDetail(
            ToDashboardTask(detail.Task),
            detail.Events.Select(item => new DashboardTaskEvent(item.Id, item.EventType, item.Payload, item.CreatedAt)).ToList());
    }

    public async Task<IReadOnlyList<DashboardStation>> GetStationsAsync(CancellationToken cancellationToken)
    {
        var stations = await client.GetFromJsonAsync<List<StationResponse>>("api/stations", cancellationToken) ?? [];
        return stations.Select(station => new DashboardStation(
            station.Code,
            station.Name,
            station.AgvStationId,
            station.Enabled)).ToList();
    }

    public async Task<DashboardPlannedPath> PlanPathAsync(
        string fromStationId,
        string toStationId,
        IReadOnlyCollection<string>? blockedStations,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            "api/planning/path",
            new PlanPathRequest(fromStationId, toStationId, blockedStations),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var path = await response.Content.ReadFromJsonAsync<ContractPlannedPath>(cancellationToken)
            ?? throw new InvalidOperationException("MES returned no planned path.");
        return new DashboardPlannedPath(path.Stations, path.Cost);
    }

    public async Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await client.GetFromJsonAsync<ContractAgvSnapshot>("api/agv", cancellationToken)
            ?? throw new InvalidOperationException("MES returned no AGV snapshot.");
        return ToDashboardSnapshot(snapshot);
    }

    public async Task<IReadOnlyList<AgvDashboardSnapshot>> GetAgvFleetAsync(CancellationToken cancellationToken)
    {
        var snapshots = await client.GetFromJsonAsync<List<ContractAgvSnapshot>>("api/agvs/fleet", cancellationToken) ?? [];
        return snapshots.Select(ToDashboardSnapshot).ToList();
    }

    public async Task<AgvCommandResult?> ExecuteAgvCommandAsync(
        string agvId,
        string command,
        Guid? taskId,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            $"api/agvs/{Uri.EscapeDataString(agvId)}/command",
            new AgvCommandRequest(command, taskId),
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ContractAgvTask>(cancellationToken);
        return result is null ? null : ToCommandResult(result);
    }

    public Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken) =>
        Task.FromException<DashboardTask>(new InvalidOperationException(
            "Task creation requires source and target station parameters."));

    public Task<DashboardTask> CreateTaskAsync(
        int sourceStationCode,
        int targetStationCode,
        int priority,
        string? description,
        string? externalId,
        CancellationToken cancellationToken) =>
        PostAsync("api/tasks", new CreateTaskRequest(sourceStationCode, targetStationCode, priority, description, externalId), cancellationToken);

    public Task<DashboardTask> DispatchTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        PostAsync($"api/tasks/{taskId}/dispatch", null, cancellationToken);

    public Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken) =>
        PostAsync($"api/tasks/{taskId}/arrived", null, cancellationToken);

    public Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) =>
        PostAsync($"api/tasks/{taskId}/confirm-pickup", new OperatorActionRequest(operatorName), cancellationToken);

    public Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) =>
        PostAsync($"api/tasks/{taskId}/confirm-dropoff", new OperatorActionRequest(operatorName), cancellationToken);

    public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken) =>
        PostAsync($"api/tasks/{taskId}/retry", null, cancellationToken);

    public Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken) =>
        PostAsync($"api/tasks/{taskId}/recover", null, cancellationToken);

    public Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) =>
        PostAsync($"api/tasks/{taskId}/cancel", new OperatorActionRequest(operatorName), cancellationToken);

    private async Task<DashboardTask> PostAsync(string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(path, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        var task = await response.Content.ReadFromJsonAsync<ContractTaskResponse>(cancellationToken)
            ?? throw new InvalidOperationException("MES returned no task.");
        return ToDashboardTask(task);
    }

    private static DashboardTask ToDashboardTask(ContractTaskResponse task) => new(
        task.Id,
        task.SourceStationCode,
        task.TargetStationCode,
        task.Status,
        task.RetryCount,
        task.LastError,
        task.Priority,
        task.Description,
        task.ExternalId,
        task.CreatedAt,
        task.EndedAt,
        task.ActiveAgvId,
        task.ActiveDeviceTaskId,
        task.ActivePath);

    private static AgvDashboardSnapshot ToDashboardSnapshot(ContractAgvSnapshot snapshot) => new(
        snapshot.Online,
        snapshot.ControlOwner,
        snapshot.CurrentStationId,
        snapshot.CurrentTaskId,
        snapshot.AgvId,
        snapshot.Capabilities ?? AgvCapabilitiesResponse.Standard);

    private static AgvCommandResult ToCommandResult(ContractAgvTask task) => new(
        task.TaskId,
        task.DeviceTaskId,
        task.TargetStationId,
        task.State,
        task.LastError,
        task.AgvId,
        task.Path);

    private static KpiDashboard ToKpiDashboard(ContractKpiDashboard dashboard) => new(
        dashboard.Date,
        new KpiTaskSummary(
            dashboard.TaskSummary.Total,
            dashboard.TaskSummary.Running,
            dashboard.TaskSummary.Completed,
            dashboard.TaskSummary.Failed,
            dashboard.TaskSummary.Cancelled),
        dashboard.TaskTrend.Select(point => new KpiTaskTrendPoint(point.Hour, point.Created, point.Completed)).ToList(),
        new KpiSampleSummary(
            dashboard.SampleSummary.Total,
            dashboard.SampleSummary.Waiting,
            dashboard.SampleSummary.Processing,
            dashboard.SampleSummary.Completed,
            dashboard.SampleSummary.Failed,
            dashboard.SampleSummary.Cancelled,
            dashboard.SampleSummary.DataSource),
        dashboard.Consumables.Select(item => new KpiConsumable(item.Name, item.Remaining, item.Capacity, item.Status, item.DataSource)).ToList(),
        dashboard.Instruments.Select(item => new KpiInstrumentStatus(item.Name, item.Status, item.Online, item.Detail, item.DataSource)).ToList());
}
