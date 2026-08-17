using System.Net.Http.Json;
using MesControlAgv.Application;

namespace MesControlAgv.Mes.Services;

public sealed class IonChromatographyGatewayClient(HttpClient client) : IIonChromatographyStatusReader
{
    public async Task<IonChromatographyStatusSnapshot> GetStatusAsync(
        string instrumentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instrumentId))
            throw new ArgumentException("Instrument ID is required.", nameof(instrumentId));

        using var response = await client.GetAsync(
            $"api/instruments/{Uri.EscapeDataString(instrumentId)}/status",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Instrument gateway returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var envelope = await response.Content.ReadFromJsonAsync<GatewayStatusEnvelope>(cancellationToken)
            ?? throw new InvalidOperationException("Instrument gateway returned no status payload.");
        return envelope.Status;
    }

    private sealed record GatewayStatusEnvelope(IonChromatographyStatusSnapshot Status);
}
