using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Mes.Services;

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

public sealed class AdapterClient(HttpClient client) : IAgvGateway, IPathAwareAgvGateway, IFleetAwareAgvGateway, IPhysicalPreflightAgvGateway, IFieldNavigationAcceptanceGateway
{
    public async Task<AgvTaskResponse> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken)
        => await DispatchAsync(operationId, null, targetStationId, cancellationToken);

    public async Task<AgvTaskResponse> DispatchAsync(
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
        return await response.Content.ReadFromJsonAsync<AgvTaskResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Adapter returned no dispatch result.");
    }

    public async Task<AgvTaskResponse> DispatchAsync(
        Guid operationId,
        string sourceStationId,
        string targetStationId,
        IReadOnlyList<string> plannedPath,
        CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync(
            $"tasks/{operationId}/dispatch",
            new { sourceStationId, targetStationId, path = plannedPath },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AgvTaskResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Adapter returned no dispatch result.");
    }

    public async Task<AgvTaskResponse?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync($"tasks/{operationId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AgvTaskResponse>(cancellationToken);
    }

    public async Task<AgvTaskResponse?> CancelAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var response = await client.PostAsync($"tasks/{operationId}/cancel", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AgvTaskResponse>(cancellationToken);
    }

    public async Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<AgvSnapshotResponse>("agv/snapshot", cancellationToken)
        ?? throw new InvalidOperationException("Adapter returned no AGV snapshot.");

    public async Task<AgvTaskResponse?> ExecuteAgvCommandAsync(
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
        return await response.Content.ReadFromJsonAsync<AgvTaskResponse>(cancellationToken);
    }

    public async Task<IReadOnlyList<AgvSnapshotResponse>> GetFleetSnapshotAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<IReadOnlyList<AgvSnapshotResponse>>("agvs", cancellationToken)
        ?? throw new InvalidOperationException("Adapter returned no AGV fleet.");

    public async Task<PhysicalAgvPreflightResponse> GetPhysicalPreflightAsync(CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<PhysicalAgvPreflightResponse>("physical/preflight", cancellationToken)
        ?? throw new InvalidOperationException("Adapter returned no physical preflight result.");

    public async Task<AgvTaskResponse> DispatchFieldNavigationAcceptanceAsync(
        Guid acceptanceId,
        FieldNavigationDispatchCommand command,
        CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync(
            $"field-navigation-acceptances/{acceptanceId}/dispatch",
            command,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AgvTaskResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Adapter returned no field navigation dispatch result.");
    }

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


