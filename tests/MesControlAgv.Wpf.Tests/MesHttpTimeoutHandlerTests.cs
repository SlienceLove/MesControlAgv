using System.Net;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class MesHttpTimeoutHandlerTests
{
    [Fact]
    public async Task Ordinary_read_uses_the_short_timeout()
    {
        using var client = CreateClient(new DelayHandler(TimeSpan.FromSeconds(5)));

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.GetAsync("api/agv"));

        Assert.Contains("GET", exception.Message, StringComparison.Ordinal);
        Assert.Contains("/api/agv", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET", "api/robot-arms/ARM-01/programs")]
    [InlineData("POST", "api/robot-arms/ARM-01/program/run")]
    public async Task Catalog_and_write_requests_keep_the_larger_budget(string method, string path)
    {
        using var client = CreateClient(new DelayHandler(TimeSpan.FromMilliseconds(80)));
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpClient CreateClient(HttpMessageHandler inner) => new(
        new MesHttpTimeoutHandler(
            readTimeout: TimeSpan.FromMilliseconds(40),
            catalogTimeout: TimeSpan.FromSeconds(1),
            writeTimeout: TimeSpan.FromSeconds(1),
            innerHandler: inner))
    {
        BaseAddress = new Uri("http://127.0.0.1:5045/"),
        Timeout = Timeout.InfiniteTimeSpan
    };

    private sealed class DelayHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
