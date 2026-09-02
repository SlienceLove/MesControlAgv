using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public sealed class AdapterIoClientTests
{
    [Fact]
    public async Task Get_io_uses_adapter_projection_and_maps_points()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new AgvIoSnapshotResponse(
            [new AgvIoPointResponse(0, "normal", true, true)],
            [new AgvIoPointResponse(6, "normal", false)],
            DateTimeOffset.Parse("2026-08-27T03:00:00Z"))));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://adapter/") };
        var client = new AdapterIoClient(httpClient);

        var snapshot = await client.GetIoAsync("AGV-01", CancellationToken.None);

        Assert.True(snapshot.DigitalInputs[0].Status);
        Assert.False(snapshot.DigitalOutputs[0].Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/agv/io", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Set_do_posts_status_to_agv_specific_adapter_route()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new AgvDoWriteResponse(
            6,
            true,
            0,
            null,
            DateTimeOffset.Parse("2026-08-27T03:00:00Z"))));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://adapter/") };
        var client = new AdapterIoClient(httpClient);

        var result = await client.SetDoAsync("AGV-01", 6, true, CancellationToken.None);

        Assert.Equal(6, result.Id);
        Assert.True(result.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/agvs/AGV-01/io/do/6", request.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.True(body.RootElement.GetProperty("status").GetBoolean());
    }

    private static HttpResponseMessage JsonResponse<T>(T value) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value)
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri, body));
            return responseFactory(request);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri? RequestUri, string? Body);
}
