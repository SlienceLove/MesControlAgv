using System.Net.Http;
using System.Text.Json;

namespace MesControlAgv.Wpf.Services;

public interface ISimulatorControlClient
{
    Task ApplyControlAsync(string mode, CancellationToken cancellationToken);
    Task ApplyControlAsync(Guid deviceTaskId, string mode, CancellationToken cancellationToken);
}

public sealed class SimulatorControlClient(HttpClient client) : ISimulatorControlClient
{
    public async Task ApplyControlAsync(string mode, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync($"controls/{mode}", content: null, cancellationToken);
        await EnsureSuccessWithDetailAsync(response, cancellationToken);
    }

    public async Task ApplyControlAsync(Guid deviceTaskId, string mode, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync($"controls/{mode}/{deviceTaskId}", content: null, cancellationToken);
        await EnsureSuccessWithDetailAsync(response, cancellationToken);
    }

    private static async Task EnsureSuccessWithDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        string? detail = null;
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("detail", out var detailElement)
                && detailElement.ValueKind == JsonValueKind.String)
            {
                detail = detailElement.GetString();
            }
        }
        catch (JsonException)
        {
        }

        throw new HttpRequestException(
            detail ?? response.ReasonPhrase ?? "Simulator request failed.",
            inner: null,
            statusCode: response.StatusCode);
    }
}
