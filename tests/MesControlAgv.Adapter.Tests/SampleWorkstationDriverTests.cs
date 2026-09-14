using System.Net;
using System.Text;
using MesControlAgv.Adapter.Modules.SampleWorkstation;
using MesControlAgv.Adapter.Modules;
using MesControlAgv.Contracts;
using Microsoft.Extensions.Configuration;

namespace MesControlAgv.Adapter.Tests;

public sealed class SampleWorkstationDriverTests
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
            """{"Code":200,"Data":[{"R_No":16,"TaskNo":"20260811-1","TaskName":"测试1","State":"任务完成 ","RequestTime":null,"ProductionTime":"2026-08-11 14:01:00","CompletionTime":"2026-08-11 14:41:00","OperatorAccount":"operator","MakeTime":"2026-08-11 14:41:37","Remark":"完成"}]}""",
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
        Assert.Null(details.RequestTime);
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
    public async Task Material_parameter_list_preserves_required_empty_date_keys()
    {
        var handler = new StubHttpHandler("""{"Code":200,"Data":[]}""");
        var driver = CreateDriver(handler);

        _ = await driver.GetProtocolReadAsync(
            "SAMPLE-WORKSTATION-01",
            SampleWorkstationProtocolOperation.MaterialTypeParameterList,
            new SampleWorkstationProtocolReadQuery(StartNo: 1, RecordNum: 5),
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("StartTime=", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("EndTime=", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("StartNo=1", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("RecordNum=5", request.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protocol_read_maps_solvent_list_and_keeps_vendor_data()
    {
        var handler = new StubHttpHandler("""{"Code":"200","Data":[{"LiquidCode":"WATER"}]}""");
        var driver = CreateDriver(handler);

        var response = await driver.GetProtocolReadAsync(
            "SAMPLE-WORKSTATION-01",
            SampleWorkstationProtocolOperation.SolventParameterList,
            new SampleWorkstationProtocolReadQuery("", "2026-08-11", "2026-08-12", 2, 10),
            CancellationToken.None);

        Assert.Equal(200, response.Code);
        Assert.Equal("WATER", response.Data[0].GetProperty("LiquidCode").GetString());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/Service/GetSolventParameterList", request.Uri.AbsolutePath);
        Assert.Contains("StartTime=2026-08-11", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("EndTime=2026-08-12", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("StartNo=2", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("RecordNum=10", request.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protocol_read_requires_a_key_for_detail_operations()
    {
        var driver = CreateDriver(new StubHttpHandler());

        await Assert.ThrowsAsync<ArgumentException>(() => driver.GetProtocolReadAsync(
            "SAMPLE-WORKSTATION-01",
            SampleWorkstationProtocolOperation.SolventParameterDetails,
            new SampleWorkstationProtocolReadQuery(),
            CancellationToken.None));
    }

    [Fact]
    public async Task Control_commands_map_to_documented_vendor_get_routes()
    {
        var handler = new StubHttpHandler(
            """{"Code":200,"Data":"正在进行初始化"}""",
            """{"Code":200,"Data":"启动成功"}""");
        var driver = CreateDriver(handler, controlEnabled: true);

        var initialized = await driver.InitializeAsync("SAMPLE-WORKSTATION-01", CancellationToken.None);
        var started = await driver.StartTaskAsync(
            "SAMPLE-WORKSTATION-01",
            "TASK-01",
            CancellationToken.None);

        Assert.Equal(SampleWorkstationCommandOperation.Initialize, initialized.Operation);
        Assert.Equal(SampleWorkstationCommandOperation.StartTask, started.Operation);
        Assert.True(initialized.Acknowledged);
        Assert.True(started.Acknowledged);
        Assert.Equal("TASK-01", started.TaskNo);
        Assert.Equal("/Service/Init", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("/Service/StartExperiment", handler.Requests[1].Uri.AbsolutePath);
        Assert.Contains("TaskNo=TASK-01", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task Start_failure_text_is_not_reported_as_a_successful_command()
    {
        var driver = CreateDriver(new StubHttpHandler(
            """{"Code":200,"Data":"启动失败"}"""), controlEnabled: true);

        var exception = await Assert.ThrowsAsync<SampleWorkstationProtocolException>(() =>
            driver.StartTaskAsync("SAMPLE-WORKSTATION-01", "TASK-01", CancellationToken.None));

        Assert.Contains("启动失败", exception.Message, StringComparison.Ordinal);
        Assert.Equal(SampleWorkstationErrorCodes.CommandUnconfirmed, exception.ErrorCode);
        Assert.Equal(200, exception.VendorCode);
        Assert.Equal("启动失败", exception.VendorData!.Value.GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("\"unexpected\"")]
    public async Task Code_200_without_known_command_acknowledgement_remains_unconfirmed(string data)
    {
        var handler = new StubHttpHandler($$"""{"Code":200,"Data":{{data}}}""");
        var driver = CreateDriver(handler, controlEnabled: true);
        var exception = await Assert.ThrowsAsync<SampleWorkstationProtocolException>(() =>
            driver.StartTaskAsync("SAMPLE-WORKSTATION-01", "TASK-01", CancellationToken.None));
        Assert.Equal(SampleWorkstationErrorCodes.CommandUnconfirmed, exception.ErrorCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Direct_driver_calls_cannot_bypass_control_switch()
    {
        var handler = new StubHttpHandler();
        var driver = CreateDriver(handler);
        await Assert.ThrowsAsync<DeviceControlDisabledException>(() =>
            driver.InitializeAsync("SAMPLE-WORKSTATION-01", CancellationToken.None));
        await Assert.ThrowsAsync<DeviceControlDisabledException>(() =>
            driver.StartTaskAsync("SAMPLE-WORKSTATION-01", "TASK-01", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Capabilities_are_offline_metadata_and_do_not_advertise_missing_vendor_apis_or_import()
    {
        var handler = new StubHttpHandler();
        var driver = CreateDriver(handler);
        var capabilities = await driver.GetCapabilitiesAsync("SAMPLE-WORKSTATION-01", CancellationToken.None);
        Assert.Equal("AdapterConfiguration", capabilities.Source);
        Assert.Empty(capabilities.Commands);
        Assert.False(capabilities.TaskImportSupported);
        Assert.DoesNotContain(SampleWorkstationProtocolOperation.WorkflowList, capabilities.ProtocolReads);
        Assert.Contains(SampleWorkstationProtocolOperation.SolventParameterList, capabilities.ProtocolReads);
        var exception = await Assert.ThrowsAsync<SampleWorkstationProtocolException>(() => driver.GetProtocolReadAsync(
            "SAMPLE-WORKSTATION-01", SampleWorkstationProtocolOperation.WorkflowList, new(), CancellationToken.None));
        Assert.Equal(SampleWorkstationErrorCodes.UnsupportedOperation, exception.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(0, 5, null, null)]
    [InlineData(1, 101, null, null)]
    [InlineData(1, 5, "bad-date", null)]
    [InlineData(1, 5, "2026-09-14", "2026-09-01")]
    public async Task Protocol_paging_validates_parameters_before_sending(int start, int count, string? from, string? to)
    {
        var handler = new StubHttpHandler();
        var driver = CreateDriver(handler);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => driver.GetProtocolReadAsync(
            "SAMPLE-WORKSTATION-01", SampleWorkstationProtocolOperation.MaterialTypeParameterList,
            new(StartDate: from, EndDate: to, StartNo: start, RecordNum: count), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("\"0\"", 0, true)]
    [InlineData("99", 99, false)]
    public async Task Error_codes_keep_numeric_strings_and_unknown_raw_values(string data, int expected, bool recognized)
    {
        var driver = CreateDriver(new StubHttpHandler($$"""{"Code":200,"Data":{{data}}}"""));
        var response = await driver.GetErrorsAsync("SAMPLE-WORKSTATION-01", CancellationToken.None);
        Assert.Equal(expected, response.ErrorCode);
        Assert.Equal(recognized, response.Recognized);
        Assert.Equal(data, response.RawData!.Value.GetRawText());
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
    public void Options_require_enabled_device_and_equipment_number_for_control()
    {
        var validControl = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Devices:SampleWorkstation:Enabled"] = "true",
                ["Devices:SampleWorkstation:ControlEnabled"] = "true",
                ["Devices:SampleWorkstation:EquipmentNo"] = "EQ-01"
            })
            .Build();
        var controlWithoutDevice = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Devices:SampleWorkstation:ControlEnabled"] = "true",
                ["Devices:SampleWorkstation:EquipmentNo"] = "EQ-01"
            })
            .Build();
        var missingEquipment = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Devices:SampleWorkstation:Enabled"] = "true"
            })
            .Build();

        var options = SampleWorkstationOptions.BindAndValidate(validControl);
        Assert.True(options.ControlEnabled);
        Assert.Equal(20000, options.RequestTimeoutMs);
        Assert.Throws<InvalidOperationException>(() =>
            SampleWorkstationOptions.BindAndValidate(controlWithoutDevice));
        Assert.Throws<InvalidOperationException>(() =>
            SampleWorkstationOptions.BindAndValidate(missingEquipment));
    }

    private static SampleWorkstationDriver CreateDriver(StubHttpHandler handler, bool controlEnabled = false)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://workstation.test/Service/")
        };
        var vendor = new VendorSampleWorkstationHttpClient(client);
        return new SampleWorkstationDriver(
            vendor,
            new SampleWorkstationOptions
            {
                Enabled = true,
                ControlEnabled = controlEnabled,
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
