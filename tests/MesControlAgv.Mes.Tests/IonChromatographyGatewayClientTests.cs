using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Application;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public sealed class IonChromatographyGatewayClientTests
{
    [Fact]
    public async Task GetStatus_uses_only_the_gateway_read_endpoint_and_maps_envelope()
    {
        var status = new IonChromatographyStatusSnapshot(
            "CIC-D160-01",
            "CIC-D160+",
            "YA7261078",
            true,
            "ReadOnlyObserved",
            false,
            DateTimeOffset.Parse("2026-08-17T07:40:59Z"),
            ColumnTemperature: 31.23,
            Conductivity: 261.885712,
            TotalConductivity: 261.885712,
            Flow: 0.3,
            MappingConfidence: "VendorDocumentAndCaptureCorrelated",
            FlowSetpoint: 0.7,
            ColumnTemperatureSetpoint: 35,
            TemperatureControlStateRaw: 0,
            PumpStateRaw: 0,
            PressureRaw: 0,
            SuppressorEluentStateRaw: 0,
            FaultCode1Raw: 0,
            FaultCode2Raw: 0);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { status })
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.local/") };
        var client = new IonChromatographyGatewayClient(httpClient);

        var actual = await client.GetStatusAsync("CIC-D160-01", CancellationToken.None);

        Assert.Equal(status, actual);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/instruments/CIC-D160-01/status", request.RequestUri!.AbsolutePath);
        Assert.Null(request.Content);
    }

    [Fact]
    public async Task Disabled_gateway_is_preserved_as_service_failure()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.local/") };
        var client = new IonChromatographyGatewayClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetStatusAsync("CIC-D160-01", CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responseFactory(request));
        }
    }
}
