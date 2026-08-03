using System.Net.Http;
using System.Net.Http.Json;

namespace MesControlAgv.Wpf.Services;

public sealed class MesClient(HttpClient client) : IMesClient
{
    public async Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<List<DashboardTask>>("api/tasks", cancellationToken)
        ?? [];

    public async Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"api/tasks/{taskId}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DashboardTaskDetail>(cancellationToken)
            ?? throw new InvalidOperationException("MES returned no task detail.");
    }

    public async Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<AgvDashboardSnapshot>("api/agv", cancellationToken)
        ?? throw new InvalidOperationException("MES returned no AGV snapshot.");

    public Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken) =>
        PostAsync("api/tasks", new { sourceStationCode = 2, targetStationCode = 4 }, cancellationToken);

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
        var response = await client.PostAsJsonAsync(path, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DashboardTask>(cancellationToken)
            ?? throw new InvalidOperationException("MES returned no task.");
    }
}
