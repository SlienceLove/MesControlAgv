using System.Net;
using System.Text;
using MesControlAgv.Adapter.Modules.SampleWorkstation;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Devices;

namespace MesControlAgv.Adapter.Tests;

public sealed class SampleWorkstationControlledDriverTests
{
    [Fact]
    public async Task CreateTask_uses_fixed_vendor_query_and_replays_completed_result_without_another_write()
    {
        var handler = new StubHttpHandler(
            "{\"Code\":200,\"Data\":0}",
            "{\"Code\":200,\"Data\":\"添加任务成功\"}");
        var driver = CreateDriver(handler);
        var operationId = Guid.NewGuid();
        var request = new SampleWorkstationTaskCreateRequest
        {
            OperationId = operationId,
            RunId = Guid.NewGuid(),
            NodeExecutionId = Guid.NewGuid(),
            OperatorName = "operator-1",
            TaskNo = "20260911-001",
            TaskName = "single-sample",
            TemplateVersion = "template-v1"
        };

        var result = await driver.CreateTaskAsync("SAMPLE-WORKSTATION-01", request, CancellationToken.None);
        var replay = await driver.CreateTaskAsync("SAMPLE-WORKSTATION-01", request, CancellationToken.None);

        Assert.Equal(result.OperationId, replay.OperationId);
        Assert.Equal(result.Status, replay.Status);
        Assert.Equal(result.VendorTaskId, replay.VendorTaskId);

        Assert.Equal(DeviceOperationLifecycle.Completed, result.Status);
        Assert.Equal(request.TaskNo, result.VendorTaskId);
        Assert.Equal(2, handler.Requests.Count); // one read-only preflight + one vendor write
        Assert.All(handler.Requests, item => Assert.Equal(HttpMethod.Get, item.Method));
        Assert.Contains("TaskNo=20260911-001", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("TaskName=single-sample", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartExperiment_transport_timeout_is_unknown_and_not_retried()
    {
        var handler = new StubHttpHandler(
            "{\"Code\":200,\"Data\":0}",
            new TaskCanceledException("vendor timeout"));
        var driver = CreateDriver(handler);
        var request = new SampleWorkstationStartRequest
        {
            OperationId = Guid.NewGuid(),
            RunId = Guid.NewGuid(),
            NodeExecutionId = Guid.NewGuid(),
            OperatorName = "operator-1",
            TaskNo = "20260911-002"
        };

        var exception = await Assert.ThrowsAsync<SampleWorkstationOperationUnknownException>(() =>
            driver.StartExperimentAsync("SAMPLE-WORKSTATION-01", request, CancellationToken.None));
        var replayException = await Assert.ThrowsAsync<SampleWorkstationOperationUnknownException>(() =>
            driver.StartExperimentAsync("SAMPLE-WORKSTATION-01", request, CancellationToken.None));

        Assert.Equal(MesControlAgv.Contracts.Devices.UnknownReason.Timeout, exception.Reason);
        Assert.Equal(exception.Reason, replayException.Reason);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static SampleWorkstationControlledDriver CreateDriver(StubHttpHandler handler)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://workstation.test/Service/")
        };
        var vendor = new VendorSampleWorkstationHttpClient(client);
        var options = new SampleWorkstationOptions
        {
            Enabled = true,
            ControlEnabled = true,
            ProtocolConfirmed = true,
            DeviceId = "SAMPLE-WORKSTATION-01",
            EquipmentNo = "EQ-01",
            BaseUrl = client.BaseAddress.ToString(),
            OperationJournalPath = Path.Combine(Path.GetTempPath(), $"mes-workstation-{Guid.NewGuid():N}.json")
        };
        var reader = new SampleWorkstationDriver(vendor, options, TimeProvider.System);
        return new SampleWorkstationControlledDriver(
            vendor,
            reader,
            options,
            TimeProvider.System,
            new SampleWorkstationOperationJournal(options));
    }

    private sealed class StubHttpHandler(params object[] responses) : HttpMessageHandler
    {
        private readonly Queue<object> _responses = new(responses);
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!));
            var next = _responses.Count > 0
                ? _responses.Dequeue()
                : throw new InvalidOperationException("No stub response remains.");
            if (next is Exception exception) throw exception;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent((string)next, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri);
}
