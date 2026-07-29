using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Domain;

namespace MesControlAgv.Mes.Services;

public sealed record AdapterTask(Guid TaskId, string DeviceTaskId, string TargetStationId, string State, string? LastError);
public sealed record AdapterSnapshot(bool Online, string ControlOwner, string? CurrentStationId, Guid? CurrentTaskId);

public interface IAdapterClient
{
    Task<AdapterTask> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken);
    Task<AdapterTask?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken);
    Task<AdapterTask?> CancelAsync(Guid operationId, CancellationToken cancellationToken);
    Task<AdapterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

public sealed class AdapterClient(HttpClient client) : IAdapterClient
{
    public async Task<AdapterTask> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync($"tasks/{operationId}/dispatch", new { targetStationId }, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdapterTask>(cancellationToken)
            ?? throw new InvalidOperationException("Adapter returned no dispatch result.");
    }

    public async Task<AdapterTask?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync($"tasks/{operationId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdapterTask>(cancellationToken);
    }

    public async Task<AdapterTask?> CancelAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var response = await client.PostAsync($"tasks/{operationId}/cancel", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AdapterTask>(cancellationToken);
    }

    public async Task<AdapterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<AdapterSnapshot>("agv/snapshot", cancellationToken)
        ?? throw new InvalidOperationException("Adapter returned no AGV snapshot.");
}

public static class TransportOperationIds
{
    public static Guid Pickup(Guid taskId) => Derive(taskId, "pickup");
    public static Guid Dropoff(Guid taskId) => Derive(taskId, "dropoff");

    private static Guid Derive(Guid taskId, string leg)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{taskId:N}:{leg}"));
        return new Guid(bytes[..16]);
    }
}
