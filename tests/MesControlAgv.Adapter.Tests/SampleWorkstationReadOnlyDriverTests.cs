using System.Net;
using System.Text;
using MesControlAgv.Adapter.Modules.SampleWorkstation;
using MesControlAgv.Contracts;
using Microsoft.Extensions.Configuration;

namespace MesControlAgv.Adapter.Tests;

public sealed class SampleWorkstationReadOnlyDriverTests
{
    [Theory]
    [InlineData(0, SampleWorkstationDeviceState.Idle, true)]
    [InlineData(1, SampleWorkstationDeviceState.Running, true)]
    [InlineData(5, SampleWorkstationDeviceState.Offline, false)]
    [InlineData(99, SampleWorkstationDeviceState.Unknown, true)]
    public async Task Status_maps_documented_values_and_sends_only_get(
        int rawState,
        SampleWorkstationDeviceState expected,
        bool online)
    {
        var handler = new StubHttpHandler($$"""{"Code":200,"Data":{{rawState}}}""");
        var driver = CreateDriver(handler);

        var status = await driver.GetStatusAsync("SAMPLE-WORKSTATION-01", CancellationToken.None);

        Assert.Equal(expected, status.State);
        Assert.Equal(online, status.Online);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/Service/GetInstrumentStatus", request.Uri.AbsolutePath);
        Assert.Contains("EquipmentNo=EQ-01", request.Uri.Query, StringComparison.Ordinal);
        Assert.True(request.NoCache);
        Assert.True(request.NoStore);
    }

    [Fact]
    public async Task Task_queries_normalize_filters_and_documented_payloads()
    {
        var handler = new StubHttpHandler(
            """{"Code":200,"Data":[{"R_No":16,"TaskNo":"20260811-1","TaskName":"测试1","State":"任务完成 ","MakeTime":"2026-08-11 14:41:37","Remark":"完成"}]}""",
            """{"Code":200,"Data":[{"R_No":16,"TaskNo":"20260811-1","TaskName":"测试1","State":"任务完成 ","RequestTime":"2026-08-11 14:00:00","ProductionTime":"2026-08-11 14:01:00","CompletionTime":"2026-08-11 14:41:00","OperatorAccount":"operator","MakeTime":"2026-08-11 14:41:37","Remark":"完成"}]}""",
            """{"Code":200,"Data":"正在运行 "}""");
        var driver = CreateDriver(handler);

        var tasks = await driver.GetTasksAsync(
            "SAMPLE-WORKSTATION-01",
            new SampleWorkstationTaskQuery("Completed", "2026-08-11", "2026-08-12", 1, 20),
            CancellationToken.None);
        var details = await driver.GetTaskDetailsAsync(
            "SAMPLE-WORKSTATION-01",
            "20260811-1",
            CancellationToken.None);
        var state = await driver.GetTaskStateAsync(
            "SAMPLE-WORKSTATION-01",
            "20260811-1",
            CancellationToken.None);

        var task = Assert.Single(tasks);
        Assert.Equal(SampleWorkstationTaskState.Completed, task.State);
        Assert.Equal("任务完成", task.RawState);
        Assert.Equal("operator", details.OperatorAccount);
        Assert.Equal(SampleWorkstationTaskState.Running, state.State);
        Assert.Contains("State=%E4%BB%BB%E5%8A%A1%E5%AE%8C%E6%88%90", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("RecordNum=20", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task Undocumented_boolean_error_payload_is_not_treated_as_no_error()
    {
        var driver = CreateDriver(new StubHttpHandler("""{"Code":200,"Data":true}"""));

        var error = await driver.GetErrorsAsync("SAMPLE-WORKSTATION-01", CancellationToken.None);

        Assert.False(error.Recognized);
        Assert.Null(error.ErrorCode);
        Assert.Equal("UndocumentedVendorErrorPayload", error.Description);
    }

    [Fact]
    public async Task Vendor_business_failure_and_invalid_task_data_fail_closed()
    {
        var businessFailure = CreateDriver(new StubHttpHandler("""{"Code":500,"Data":"失败"}"""));
        var invalidData = CreateDriver(new StubHttpHandler("""{"Code":200,"Data":"not-a-list"}"""));

        await Assert.ThrowsAsync<SampleWorkstationProtocolException>(() =>
            businessFailure.GetStatusAsync("SAMPLE-WORKSTATION-01", CancellationToken.None));
        await Assert.ThrowsAsync<SampleWorkstationProtocolException>(() =>
            invalidData.GetTasksAsync(
                "SAMPLE-WORKSTATION-01",
                new SampleWorkstationTaskQuery(),
                CancellationToken.None));
    }

    [Fact]
    public void Options_reject_control_enablement_and_enabled_device_without_equipment_number()
    {
        var control = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Devices:SampleWorkstation:ControlEnabled"] = "true"
            })
            .Build();
        var missingEquipment = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Devices:SampleWorkstation:Enabled"] = "true"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = SampleWorkstationOptions.BindAndValidate(control);
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = SampleWorkstationOptions.BindAndValidate(missingEquipment);
        });
    }

    private static SampleWorkstationReadOnlyDriver CreateDriver(StubHttpHandler handler)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://workstation.test/Service/")
        };
        var vendor = new VendorSampleWorkstationHttpClient(client);
        return new SampleWorkstationReadOnlyDriver(
            vendor,
            new SampleWorkstationOptions
            {
                Enabled = true,
                DeviceId = "SAMPLE-WORKSTATION-01",
                EquipmentNo = "EQ-01",
                BaseUrl = client.BaseAddress.ToString()
            },
            new FixedTimeProvider());
    }

    private sealed class StubHttpHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.CacheControl?.NoCache == true,
                request.Headers.CacheControl?.NoStore == true));
            var body = _responses.Count > 0 ? _responses.Dequeue() : throw new InvalidOperationException("No stub response remains.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, bool NoCache, bool NoStore);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
    }
}
