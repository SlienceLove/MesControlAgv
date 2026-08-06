using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Services;

public sealed class SimulatorClient(HttpClient client) : ISimulatorClient, IAgvFleetDeviceClient
{
    public Task EnsureControlAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<AgvSnapshotResponse>("snapshot", cancellationToken)
        ?? throw new InvalidOperationException("Simulator returned no snapshot.");

    public async Task<AgvTaskResponse?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync($"tasks/{taskId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgvTaskResponse>(cancellationToken);
    }

    public async Task<AgvTaskResponse?> GetTaskAsync(
        Guid taskId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken) =>
        (await GetTaskAsync(taskId, cancellationToken)) is { } task
            ? task with { Path = path }
            : null;

    public async Task<AgvTaskResponse> NavigateAsync(Guid taskId, string? sourceStationId, string stationId, CancellationToken cancellationToken)
        => await NavigateAsync(taskId, sourceStationId, stationId, null, cancellationToken);

    public async Task<AgvTaskResponse> NavigateAsync(
        Guid taskId,
        string? sourceStationId,
        string stationId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync(
            "commands/navigate",
            new { taskId, sourceStationId, targetStationId = stationId, path },
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.GatewayTimeout) throw new TimeoutException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgvTaskResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Simulator returned no task.");
    }

    public async Task<IReadOnlyList<AgvSnapshotResponse>> GetFleetSnapshotAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<IReadOnlyList<AgvSnapshotResponse>>("agvs", cancellationToken)
        ?? throw new InvalidOperationException("Simulator returned no AGV fleet.");

    public async Task<AgvTaskResponse?> GetTaskAsync(string agvId, Guid taskId, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync($"agvs/{Uri.EscapeDataString(agvId)}/tasks/{taskId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgvTaskResponse>(cancellationToken);
    }

    public async Task<AgvTaskResponse> NavigateAsync(
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
        return await response.Content.ReadFromJsonAsync<AgvTaskResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Simulator returned no task.");
    }

    public Task<AgvTaskResponse?> PauseAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        SendFleetActionAsync(agvId, taskId, "pause", cancellationToken);

    public Task<AgvTaskResponse?> ResumeAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        SendFleetActionAsync(agvId, taskId, "resume", cancellationToken);

    public Task<AgvTaskResponse?> CancelAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        SendFleetActionAsync(agvId, taskId, "cancel", cancellationToken);

    private async Task<AgvTaskResponse?> SendFleetActionAsync(
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
        return await response.Content.ReadFromJsonAsync<AgvTaskResponse>(cancellationToken);
    }

    public async Task<AgvTaskResponse?> PauseAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var response = await client.PostAsync($"commands/{taskId}/pause", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgvTaskResponse>(cancellationToken);
    }

    public async Task<AgvTaskResponse?> ResumeAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var response = await client.PostAsync($"commands/{taskId}/resume", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgvTaskResponse>(cancellationToken);
    }

    public async Task<AgvTaskResponse?> CancelAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var response = await client.PostAsync($"commands/{taskId}/cancel", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgvTaskResponse>(cancellationToken);
    }

    public async Task<AgvTaskResponse?> PauseAsync(
        Guid taskId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken) =>
        (await PauseAsync(taskId, cancellationToken)) is { } task
            ? task with { Path = path }
            : null;

    public async Task<AgvTaskResponse?> ResumeAsync(
        Guid taskId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken) =>
        (await ResumeAsync(taskId, cancellationToken)) is { } task
            ? task with { Path = path }
            : null;

    public async Task<AgvTaskResponse?> CancelAsync(
        Guid taskId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken) =>
        (await CancelAsync(taskId, cancellationToken)) is { } task
            ? task with { Path = path }
            : null;
}
