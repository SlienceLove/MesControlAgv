using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Domain;

namespace MesControlAgv.Mes.Services;

public sealed record AdapterTask(
    Guid TaskId,
    string DeviceTaskId,
    string TargetStationId,
    string State,
    string? LastError,
    string AgvId = "AGV-01",
    IReadOnlyList<string>? Path = null);

public sealed record AdapterSnapshot(
    bool Online,
    string ControlOwner,
    string? CurrentStationId,
    Guid? CurrentTaskId,
    string AgvId = "AGV-01");

public sealed class AdapterHttpException(HttpStatusCode responseStatusCode, string? detail)
    : HttpRequestException(BuildMessage(responseStatusCode, detail), null, responseStatusCode)
{
    public HttpStatusCode ResponseStatusCode { get; } = responseStatusCode;
    public string? Detail { get; } = detail;

    private static string BuildMessage(HttpStatusCode statusCode, string? detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? $"Adapter returned HTTP {(int)statusCode} ({statusCode})."
            : $"Adapter returned HTTP {(int)statusCode} ({statusCode}): {detail}";
}

public interface IAdapterClient
{
    Task<AdapterTask> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken);
    Task<AdapterTask?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken);
    Task<AdapterTask?> CancelAsync(Guid operationId, CancellationToken cancellationToken);
    Task<AdapterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<AdapterTask?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken);
}

public interface IRouteAwareAdapterClient
{
    Task<AdapterTask> DispatchAsync(
        Guid operationId,
        string sourceStationId,
        string targetStationId,
        CancellationToken cancellationToken);
}

public interface IFleetAwareAdapterClient
{
    Task<IReadOnlyList<AdapterSnapshot>> GetFleetSnapshotAsync(CancellationToken cancellationToken);
}

public sealed class AdapterClient(HttpClient client) : IAdapterClient, IRouteAwareAdapterClient, IFleetAwareAdapterClient
{
    public async Task<AdapterTask> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken)
        => await DispatchAsync(operationId, null, targetStationId, cancellationToken);

    public async Task<AdapterTask> DispatchAsync(
        Guid operationId,
        string? sourceStationId,
        string targetStationId,
        CancellationToken cancellationToken)
    {
        object request = sourceStationId is null
            ? new { targetStationId }
            : new { sourceStationId, targetStationId };
        var response = await client.PostAsJsonAsync($"tasks/{operationId}/dispatch", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AdapterTask>(cancellationToken)
            ?? throw new InvalidOperationException("Adapter returned no dispatch result.");
    }

    public async Task<AdapterTask?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync($"tasks/{operationId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AdapterTask>(cancellationToken);
    }

    public async Task<AdapterTask?> CancelAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var response = await client.PostAsync($"tasks/{operationId}/cancel", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AdapterTask>(cancellationToken);
    }

    public async Task<AdapterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<AdapterSnapshot>("agv/snapshot", cancellationToken)
        ?? throw new InvalidOperationException("Adapter returned no AGV snapshot.");

    public async Task<AdapterTask?> ExecuteAgvCommandAsync(
        string agvId,
        string command,
        Guid? taskId,
        CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync(
            $"agvs/{Uri.EscapeDataString(agvId)}/command",
            new { command, taskId },
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AdapterTask>(cancellationToken);
    }

    public async Task<IReadOnlyList<AdapterSnapshot>> GetFleetSnapshotAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<IReadOnlyList<AdapterSnapshot>>("agvs", cancellationToken)
        ?? throw new InvalidOperationException("Adapter returned no AGV fleet.");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new AdapterHttpException(response.StatusCode, ExtractDetail(body));
    }

    private static string? ExtractDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String)
            {
                return detail.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return body;
    }
}
