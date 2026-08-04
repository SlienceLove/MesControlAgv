using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Adapter.Contracts;

namespace MesControlAgv.Adapter.Services;

public sealed class SimulatorClient(HttpClient client) : ISimulatorClient, IAgvFleetDeviceClient
{
    public Task EnsureControlAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<AgvSnapshotResponse>("snapshot", cancellationToken)
        ?? throw new InvalidOperationException("Simulator returned no snapshot.");

    public async Task<AdapterTaskResponse?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync($"tasks/{taskId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdapterTaskResponse>(cancellationToken);
    }

    public async Task<AdapterTaskResponse> NavigateAsync(Guid taskId, string? sourceStationId, string stationId, CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync("commands/navigate", new { taskId, targetStationId = stationId }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.GatewayTimeout) throw new TimeoutException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdapterTaskResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Simulator returned no task.");
    }

    public async Task<IReadOnlyList<AgvSnapshotResponse>> GetFleetSnapshotAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<IReadOnlyList<AgvSnapshotResponse>>("agvs", cancellationToken)
        ?? throw new InvalidOperationException("Simulator returned no AGV fleet.");

    public async Task<AdapterTaskResponse?> GetTaskAsync(string agvId, Guid taskId, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync($"agvs/{Uri.EscapeDataString(agvId)}/tasks/{taskId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdapterTaskResponse>(cancellationToken);
    }

    public async Task<AdapterTaskResponse> NavigateAsync(
        string agvId,
        Guid taskId,
        string? sourceStationId,
        string stationId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync(
            $"agvs/{Uri.EscapeDataString(agvId)}/commands/navigate",
            new { taskId, sourceStationId, targetStationId = stationId, path },
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.GatewayTimeout) throw new TimeoutException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdapterTaskResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Simulator returned no task.");
    }

    public Task<AdapterTaskResponse?> PauseAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        SendFleetActionAsync(agvId, taskId, "pause", cancellationToken);

    public Task<AdapterTaskResponse?> ResumeAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        SendFleetActionAsync(agvId, taskId, "resume", cancellationToken);

    public Task<AdapterTaskResponse?> CancelAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        SendFleetActionAsync(agvId, taskId, "cancel", cancellationToken);

    private async Task<AdapterTaskResponse?> SendFleetActionAsync(
        string agvId,
        Guid taskId,
        string action,
        CancellationToken cancellationToken)
    {
        var response = await client.PostAsync(
            $"agvs/{Uri.EscapeDataString(agvId)}/commands/{taskId}/{action}",
            null,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdapterTaskResponse>(cancellationToken);
    }

    public async Task<AdapterTaskResponse?> PauseAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var response = await client.PostAsync($"commands/{taskId}/pause", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdapterTaskResponse>(cancellationToken);
    }

    public async Task<AdapterTaskResponse?> ResumeAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var response = await client.PostAsync($"commands/{taskId}/resume", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdapterTaskResponse>(cancellationToken);
    }

    public async Task<AdapterTaskResponse?> CancelAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var response = await client.PostAsync($"commands/{taskId}/cancel", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdapterTaskResponse>(cancellationToken);
    }
}
