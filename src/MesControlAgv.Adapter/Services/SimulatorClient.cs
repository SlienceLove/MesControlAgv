using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Adapter.Contracts;

namespace MesControlAgv.Adapter.Services;

public sealed class SimulatorClient(HttpClient client) : ISimulatorClient
{
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

    public async Task<AdapterTaskResponse> NavigateAsync(Guid taskId, string stationId, CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync("commands/navigate", new { taskId, targetStationId = stationId }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.GatewayTimeout) throw new TimeoutException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdapterTaskResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Simulator returned no task.");
    }
}
