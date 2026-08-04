using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class SimulatorControlClientTests
{
    [Fact]
    public async Task Non_success_response_uses_json_detail_in_exception()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new { detail = "device task is already completed" })
        }))
        {
            BaseAddress = new Uri("http://simulator/")
        };
        var client = new SimulatorControlClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ApplyControlAsync("arrive", CancellationToken.None));

        Assert.Equal("device task is already completed", exception.Message);
        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
