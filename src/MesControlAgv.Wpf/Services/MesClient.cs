using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace MesControlAgv.Wpf.Services;

public sealed class MesClient(HttpClient client) : IMesClient
{
    public async Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<List<DashboardTask>>("api/tasks", cancellationToken) ?? [];

    public async Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"api/tasks/{taskId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DashboardTaskDetail>(cancellationToken)
            ?? throw new InvalidOperationException("MES returned no task detail.");
    }

    public async Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<AgvDashboardSnapshot>("api/agv", cancellationToken)
        ?? throw new InvalidOperationException("MES returned no AGV snapshot.");

    public async Task<IReadOnlyList<AgvDashboardSnapshot>> GetAgvFleetAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<List<AgvDashboardSnapshot>>("api/agvs/fleet", cancellationToken) ?? [];

    public async Task<AgvCommandResult?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync($"api/agvs/{Uri.EscapeDataString(agvId)}/command", new { command, taskId }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgvCommandResult>(cancellationToken);
    }

    public Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken) =>
        CreateTaskAsync(2, 4, 0, null, null, cancellationToken);

    public Task<DashboardTask> CreateTaskAsync(int sourceStationCode, int targetStationCode, int priority, string? description, string? externalId, CancellationToken cancellationToken) =>
        PostAsync("api/tasks", new { sourceStationCode, targetStationCode, priority, description, externalId }, cancellationToken);

    public Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken) =>
        PostAsync($"api/tasks/{taskId}/arrived", null, cancellationToken);

    public Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) =>
        PostAsync($"api/tasks/{taskId}/confirm-pickup", new { operatorName }, cancellationToken);

    public Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) =>
        PostAsync($"api/tasks/{taskId}/confirm-dropoff", new { operatorName }, cancellationToken);

    public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken) =>
        PostAsync($"api/tasks/{taskId}/retry", null, cancellationToken);

    public Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken) =>
        PostAsync($"api/tasks/{taskId}/recover", null, cancellationToken);

    public Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) =>
        PostAsync($"api/tasks/{taskId}/cancel", new { operatorName }, cancellationToken);

    private async Task<DashboardTask> PostAsync(string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(path, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DashboardTask>(cancellationToken)
            ?? throw new InvalidOperationException("MES returned no task.");
    }
}
