using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using ContractAgvSnapshot = MesControlAgv.Contracts.AgvSnapshotResponse;
using ContractAgvFleetStatus = MesControlAgv.Contracts.AgvFleetStatusResponse;
using ContractAgvTask = MesControlAgv.Contracts.AgvTaskResponse;
using ContractKpiDashboard = MesControlAgv.Contracts.KpiDashboardResponse;
using ContractPlannedPath = MesControlAgv.Contracts.PlannedPathResponse;
using ContractTaskDetail = MesControlAgv.Contracts.TaskDetailResponse;
using ContractTaskResponse = MesControlAgv.Contracts.TaskResponse;

namespace MesControlAgv.Wpf.Services;

public sealed class MesApiException(HttpStatusCode responseStatusCode, string? detail)
    : HttpRequestException(BuildMessage(responseStatusCode, detail), null, responseStatusCode)
{
    public HttpStatusCode ResponseStatusCode { get; } = responseStatusCode;
    public string? Detail { get; } = detail;

    private static string BuildMessage(HttpStatusCode statusCode, string? detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? $"MES returned HTTP {(int)statusCode} ({statusCode})."
            : $"MES returned HTTP {(int)statusCode} ({statusCode}): {detail}";
}

public sealed class MesClient(HttpClient client) : IMesClient
{
    public async Task<RuntimeReadinessResponse?> GetRuntimeReadinessAsync(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("api/runtime/readiness", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<RuntimeReadinessResponse>(cancellationToken)
            ?? throw new InvalidOperationException("MES returned no runtime readiness evidence.");
    }

    public async Task<PhysicalAgvPreflightResponse?> GetPhysicalPreflightAsync(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("api/physical/preflight", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PhysicalAgvPreflightResponse>(cancellationToken)
            ?? throw new InvalidOperationException("MES returned no physical preflight evidence.");
    }

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
        await EnsureSuccessAsync(response, cancellationToken);
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
        await EnsureSuccessAsync(response, cancellationToken);
        var path = await response.Content.ReadFromJsonAsync<ContractPlannedPath>(cancellationToken)
            ?? throw new InvalidOperationException("MES returned no planned path.");
        return new DashboardPlannedPath(path.Stations, path.Cost, path.FromStationId, path.ToStationId);
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

    public async Task<IReadOnlyList<AgvFleetDashboardStatus>> GetAgvFleetStatusAsync(CancellationToken cancellationToken)
    {
        var statuses = await client.GetFromJsonAsync<List<ContractAgvFleetStatus>>("api/agvs/fleet/status", cancellationToken) ?? [];
        return statuses.Select(status => new AgvFleetDashboardStatus(
            ToDashboardSnapshot(status.Snapshot),
            status.ActiveTask is null
                ? null
                : new AgvActiveTaskStatus(
                    status.ActiveTask.TransportTaskId,
                    status.ActiveTask.OperationId,
                    status.ActiveTask.MesStatus,
                    status.ActiveTask.DeviceTaskId,
                    status.ActiveTask.DeviceState,
                    status.ActiveTask.TargetStationId,
                    status.ActiveTask.LastError,
                    status.ActiveTask.Path))).ToList();
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
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ContractAgvTask>(cancellationToken);
        return result is null ? null : ToCommandResult(result);
    }

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

    public async Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowsAsync(CancellationToken cancellationToken)
    {
        return await client.GetFromJsonAsync<List<WorkflowDefinition>>("api/workflows", cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        return await client.GetFromJsonAsync<List<WorkflowVersion>>(
            $"api/workflows/{workflowId}/versions", cancellationToken) ?? [];
    }

    public async Task<WorkflowVersion?> GetWorkflowVersionAsync(
        Guid workflowId,
        int version,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"api/workflows/{workflowId}/versions/{version}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WorkflowVersion>(cancellationToken)
            ?? throw new InvalidOperationException("MES returned no workflow version.");
    }

    public Task<WorkflowVersion> CreateWorkflowDraftAsync(
        WorkflowDefinition definition,
        string actor,
        CancellationToken cancellationToken) =>
        PostWorkflowAsync($"api/workflows?actor={Uri.EscapeDataString(actor)}", definition, cancellationToken);

    public Task<WorkflowVersion> UpdateWorkflowDraftAsync(
        Guid workflowId,
        int version,
        WorkflowDefinition definition,
        string actor,
        CancellationToken cancellationToken) =>
        SendWorkflowAsync(
            HttpMethod.Put,
            $"api/workflows/{workflowId}/versions/{version}/draft?actor={Uri.EscapeDataString(actor)}",
            definition,
            cancellationToken);

    public Task<WorkflowValidationResult> ValidateWorkflowAsync(
        WorkflowDefinition definition,
        CancellationToken cancellationToken) =>
        PostWorkflowValidationAsync("api/workflows/validate", definition, cancellationToken);

    public Task<WorkflowValidationResult> ValidateWorkflowVersionAsync(
        Guid workflowId,
        int version,
        CancellationToken cancellationToken) =>
        PostWorkflowValidationAsync(
            $"api/workflows/{workflowId}/versions/{version}/validate",
            body: null,
            cancellationToken);

    public Task<WorkflowVersion> PublishWorkflowAsync(
        Guid workflowId,
        int version,
        string actor,
        CancellationToken cancellationToken) =>
        PostWorkflowAsync(
            $"api/workflows/{workflowId}/versions/{version}/publish?actor={Uri.EscapeDataString(actor)}",
            body: null,
            cancellationToken);

    public async Task<DashboardWorkflowExecution> ExecuteWorkflowAsync(
        WorkflowExecutionRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("api/workflows/execute", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<DashboardWorkflowExecution>(cancellationToken)
            ?? throw new InvalidOperationException("MES returned no workflow execution result.");
    }

    private async Task<DashboardTask> PostAsync(string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(path, body, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var task = await response.Content.ReadFromJsonAsync<ContractTaskResponse>(cancellationToken)
            ?? throw new InvalidOperationException("MES returned no task.");
        return ToDashboardTask(task);
    }

    private Task<WorkflowVersion> PostWorkflowAsync(
        string path,
        WorkflowDefinition? body,
        CancellationToken cancellationToken) =>
        SendWorkflowAsync(HttpMethod.Post, path, body, cancellationToken);

    private async Task<WorkflowVersion> SendWorkflowAsync(
        HttpMethod method,
        string path,
        WorkflowDefinition? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WorkflowVersion>(cancellationToken)
            ?? throw new InvalidOperationException("MES returned no workflow version.");
    }

    private async Task<WorkflowValidationResult> PostWorkflowValidationAsync(
        string path,
        WorkflowDefinition? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WorkflowValidationResult>(cancellationToken)
            ?? throw new InvalidOperationException("MES returned no workflow validation result.");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new MesApiException(response.StatusCode, ExtractDetail(body));
    }

    private static string? ExtractDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("detail", out var detail)
                    && detail.ValueKind == JsonValueKind.String)
                {
                    return detail.GetString();
                }

                if (document.RootElement.TryGetProperty("title", out var title)
                    && title.ValueKind == JsonValueKind.String)
                {
                    return title.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return body.Trim();
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
