using System.Net.Http.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// MES-side HTTP projection of the Adapter's AGV I/O capability. The Adapter
/// remains the only owner of the vendor TCP framing and control-ownership logic.
/// </summary>
public sealed class AdapterIoClient(HttpClient client) : IAgvIoGateway
{
    public async Task<AgvIoSnapshotResponse> GetIoAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        EnsureAgvId(agvId);
        using var response = await client.GetAsync("agv/io", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AgvIoSnapshotResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Adapter returned no AGV I/O snapshot.");
    }

    public async Task<AgvDoWriteResponse> SetDoAsync(
        string agvId,
        int id,
        bool status,
        CancellationToken cancellationToken)
    {
        EnsureAgvId(agvId);
        if (id < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "DO id must be non-negative.");
        }

        using var response = await client.PostAsJsonAsync(
            $"agvs/{Uri.EscapeDataString(agvId)}/io/do/{id}",
            new AgvDoWriteRequest(status),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AgvDoWriteResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Adapter returned no AGV DO write result.");
    }

    private static void EnsureAgvId(string agvId) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new AdapterHttpException(response.StatusCode, ExtractDetail(detail));
    }

    private static string? ExtractDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == System.Text.Json.JsonValueKind.String
                    ? detail.GetString()
                    : body;
        }
        catch (System.Text.Json.JsonException)
        {
            return body;
        }
    }
}
