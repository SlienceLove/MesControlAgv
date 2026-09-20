using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkstationBatchClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_client_posts_only_import_and_reports_missing_server_support(bool missing)
    {
        var input = new WorkstationBatchRequest(Guid.NewGuid(), "tester", [new("T001", "任务", [new("L", "T", 0, 0, "S", 1, 1, "D", 1, 1, 50)])]);
        var handler = new Handler(input, missing);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new MesClient(http);
        if (missing) Assert.Contains("尚未部署", (await Assert.ThrowsAsync<InvalidOperationException>(() => client.ImportWorkstationBatchAsync("WS-01", input, CancellationToken.None))).Message);
        else Assert.Equal(input.RequestId, (await client.ImportWorkstationBatchAsync("WS-01", input, CancellationToken.None)).RequestId);
        Assert.Equal(1, handler.Calls);
    }
    private sealed class Handler(WorkstationBatchRequest expected, bool missing) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/workstations/WS-01/task-batches/import", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadFromJsonAsync<WorkstationBatchRequest>(token);
            Assert.Equal(expected.RequestId, body!.RequestId);
            Assert.Equal(50, Assert.Single(Assert.Single(body.Tasks).Transfers).VolumeMicroliters);
            return missing ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) {
                Content = JsonContent.Create(new WorkstationBatchResult(expected.RequestId, "WS-01", "hash", [], DateTimeOffset.UtcNow)) };
        }
    }
}
