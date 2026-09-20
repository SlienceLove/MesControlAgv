using System.Net;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class MesHttpTimeoutHandlerTests
{
    [Fact]
    public async Task Batch_import_has_its_own_finite_budget()
    {
        using var client = new HttpClient(new MesHttpTimeoutHandler(
            writeTimeout: TimeSpan.FromMilliseconds(20),
            batchImportTimeout: TimeSpan.FromSeconds(2),
            innerHandler: new DelayHandler(TimeSpan.FromMilliseconds(100))))
        { BaseAddress = new Uri("http://localhost/"), Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.PostAsync("api/workstations/WS-01/task-batches/import", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Assert.ThrowsAsync<TimeoutException>(() => client.PostAsync("api/other", null));
    }
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
