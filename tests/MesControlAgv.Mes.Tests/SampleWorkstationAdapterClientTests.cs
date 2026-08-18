using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public sealed class SampleWorkstationAdapterClientTests
{
    [Fact]
    public async Task Read_methods_use_only_normalized_adapter_get_routes()
    {
        var observedAt = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
        var handler = new RecordingHandler(
            JsonContent.Create(new SampleWorkstationStatusResponse(
                "SAMPLE-WORKSTATION-01", "EQ-01", true, SampleWorkstationDeviceState.Idle, 0, observedAt)),
            JsonContent.Create<IReadOnlyList<SampleWorkstationTaskSummaryResponse>>(
            [
                new(1, "TASK-01", "task", SampleWorkstationTaskState.Waiting, "等待运行", "2026-08-17 10:00:00", null)
            ]));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://adapter.local/") };
        var client = new SampleWorkstationAdapterClient(httpClient);

        var status = await client.GetStatusAsync("SAMPLE-WORKSTATION-01", CancellationToken.None);
        var tasks = await client.GetTasksAsync(
            "SAMPLE-WORKSTATION-01",
            new SampleWorkstationTaskQuery("Waiting", StartNo: 1, RecordNum: 20),
            CancellationToken.None);

        Assert.Equal(SampleWorkstationDeviceState.Idle, status.State);
        Assert.Single(tasks);
        Assert.Equal("/api/workstations/SAMPLE-WORKSTATION-01/status", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/api/workstations/SAMPLE-WORKSTATION-01/tasks", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Contains("state=Waiting", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("recordNum=20", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
        });
    }

    [Fact]
    public async Task Adapter_failure_preserves_status_and_problem_detail()
    {
        var handler = new RecordingHandler(new StringContent("""{"detail":"device disabled"}"""), HttpStatusCode.ServiceUnavailable);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://adapter.local/") };
        var client = new SampleWorkstationAdapterClient(httpClient);

        var exception = await Assert.ThrowsAsync<AdapterHttpException>(() =>
            client.GetStatusAsync("SAMPLE-WORKSTATION-01", CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.ResponseStatusCode);
        Assert.Equal("device disabled", exception.Detail);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpContent Content, HttpStatusCode Status)> _responses;

        public RecordingHandler(params HttpContent[] responses) =>
            _responses = new Queue<(HttpContent, HttpStatusCode)>(
                responses.Select(content => (content, HttpStatusCode.OK)));

        public RecordingHandler(HttpContent response, HttpStatusCode status) =>
            _responses = new Queue<(HttpContent, HttpStatusCode)>([(response, status)]);

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(response.Status) { Content = response.Content });
        }
    }
}
