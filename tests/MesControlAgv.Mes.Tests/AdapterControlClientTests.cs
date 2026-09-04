using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public sealed class AdapterControlClientTests
{
    [Fact]
    public async Task Control_release_uses_the_dedicated_endpoint_once_and_reads_confirmation()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { released = true })
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://adapter.local/") };
        var client = new AdapterClient(http);

        var released = await client.ReleaseControlAsync(CancellationToken.None);

        Assert.True(released);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/agv/control/release", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Control_release_conflict_reports_not_owned_without_retry()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new { detail = "not owned" })
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://adapter.local/") };
        var client = new AdapterClient(http);

        Assert.False(await client.ReleaseControlAsync(CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Control_release_transport_failure_preserves_gateway_status_without_retry(
        HttpStatusCode statusCode)
    {
        var handler = new RecordingHandler(new HttpResponseMessage(statusCode)
        {
            Content = JsonContent.Create(new { detail = "ownership read unavailable; release not sent" })
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://adapter.local/") };
        var client = new AdapterClient(http);

        var exception = await Assert.ThrowsAsync<AdapterHttpException>(
            () => client.ReleaseControlAsync(CancellationToken.None));

        Assert.Equal(statusCode, exception.ResponseStatusCode);
        Assert.Contains("release not sent", exception.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(response);
        }
    }
}
