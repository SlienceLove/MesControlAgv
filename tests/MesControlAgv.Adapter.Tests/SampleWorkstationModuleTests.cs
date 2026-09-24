using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MesControlAgv.Adapter.Modules;
using MesControlAgv.Adapter.Modules.SampleWorkstation;
using MesControlAgv.Contracts;
using MesControlAgv.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MesControlAgv.Adapter.Tests;

public sealed class SampleWorkstationModuleTests
{
    [Fact]
    public async Task Short_task_page_preserves_rows_before_explicit_end_of_list()
    {
        var starts = new List<int>();
        using var vendor = new VendorHandler("")
        {
            ResponseFactory = request =>
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);
                var start = int.Parse(query["StartNo"].ToString());
                starts.Add(start);
                return start == 1
                    ? JsonSerializer.Serialize(new { Code = 200, Data = Enumerable.Range(1, 29).Select(i =>
                        new { R_No = i, TaskNo = $"TASK-{i}", TaskName = $"Task {i}", State = "等待运行", MakeTime = "2026-09-24 13:00:00" }) })
                    : "{\"Code\":201,\"Data\":\"无数据\"}";
            }
        };
        await using var app = await CreateHostAsync(vendor, control: false);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var first = (await client.GetFromJsonAsync<SampleWorkstationTaskSummaryResponse[]>(
            "/api/workstations/WS-01/tasks?startNo=1&recordNum=100"))!;
        Assert.Equal(29, first.Length);
        Assert.Equal(Enumerable.Range(1, 29).Select(i => $"TASK-{i}"), first.Select(row => row.TaskNo));
        var end = (await client.GetFromJsonAsync<SampleWorkstationTaskSummaryResponse[]>(
            $"/api/workstations/WS-01/tasks?startNo={1 + first.Length}&recordNum=100"))!;
        Assert.Empty(end);
        Assert.Equal(new[] { 1, 30 }, starts);
        Assert.Equal(2, vendor.RequestCount);
        Assert.All(vendor.Paths, path => Assert.Equal("/Service/GetTaskInformationList", path));
    }

    [Theory]
    [InlineData(1, "{\"Code\":201,\"Data\":\"无数据\"}")]
    [InlineData(30, "{\"Code\":201,\"Data\":\"无数据\"}")]
    [InlineData(30, "{\"code\":\"201\",\"data\":\" 无数据 \"}")]
    public async Task Task_list_explicit_no_data_is_an_empty_page(int startNo, string body)
    {
        using var vendor = new VendorHandler(body);
        await using var app = await CreateHostAsync(vendor, control: false);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.GetAsync($"/api/workstations/WS-01/tasks?startNo={startNo}&recordNum=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<SampleWorkstationTaskSummaryResponse[]>())!);
        Assert.Equal(1, vendor.RequestCount);
        Assert.Equal(HttpMethod.Get, vendor.LastMethod);
        Assert.Equal("/Service/GetTaskInformationList", vendor.LastUri!.AbsolutePath);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(vendor.LastUri.Query);
        Assert.Equal(startNo.ToString(), query["StartNo"].ToString());
        Assert.Equal("100", query["RecordNum"].ToString());
        Assert.True(query.ContainsKey("StartTime"));
        Assert.True(query.ContainsKey("EndTime"));
    }

    [Theory]
    [InlineData("{\"Code\":201,\"Data\":\"数据库错误\"}")]
    [InlineData("{\"Code\":201,\"Data\":\"查询失败，无数据\"}")]
    [InlineData("{\"Code\":201,\"Data\":null}")]
    [InlineData("{\"Code\":201,\"Data\":[]}")]
    [InlineData("{\"Code\":201,\"Data\":{}}")]
    [InlineData("{\"Code\":201,\"Data\":true}")]
    [InlineData("{\"Code\":201,\"Data\":\"\"}")]
    [InlineData("{\"Code\":500,\"Data\":\"无数据\"}")]
    [InlineData("{\"Code\":200,\"Data\":\"无数据\"}")]
    [InlineData("{\"Data\":\"无数据\"}")]
    public async Task Task_list_does_not_hide_other_failures_as_empty(string body)
    {
        using var vendor = new VendorHandler(body);
        await using var app = await CreateHostAsync(vendor, control: false);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.GetAsync("/api/workstations/WS-01/tasks?startNo=30&recordNum=100");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, vendor.RequestCount);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("tasks/TASK-01")]
    [InlineData("tasks/TASK-01/state")]
    [InlineData("tasks/TASK-01/template")]
    [InlineData("protocol/MaterialTypeParameterList")]
    public async Task No_data_on_other_reads_remains_a_failure(string path)
    {
        using var vendor = new VendorHandler("{\"Code\":201,\"Data\":\"无数据\"}");
        await using var app = await CreateHostAsync(vendor, control: false);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.GetAsync($"/api/workstations/WS-01/{path}");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, vendor.RequestCount);
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("tasks/TASK-01/start")]
    [InlineData("tasks/TASK-01/barcodes")]
    [InlineData("tasks/import?fileName=task.xlsx")]
    public async Task No_data_never_acknowledges_a_write_or_retries_it(string path)
    {
        using var vendor = new VendorHandler("{\"Code\":201,\"Data\":\"无数据\"}");
        await using var app = await CreateHostAsync(vendor, control: true);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using HttpContent content = path.Contains("import", StringComparison.Ordinal)
            ? new ByteArrayContent(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "workstation-test-001.xlsx")))
            : JsonContent.Create(new SampleWorkstationTaskBarcodes { SampleBarcode1 = "A", SampleBarcode2 = "B" });
        if (path.Contains("import", StringComparison.Ordinal))
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PostAsync($"/api/workstations/WS-01/{path}", content);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, vendor.RequestCount);
    }

    [Theory]
    [InlineData(SampleWorkstationTemplateVolumeUnit.Microliters)]
    [InlineData(SampleWorkstationTemplateVolumeUnit.Milliliters)]
    public async Task Capabilities_advertise_configured_template_unit_without_vendor_calls(SampleWorkstationTemplateVolumeUnit unit)
    {
        using var vendor = new VendorHandler("{}");
        await using var app = await CreateHostAsync(vendor, control: true, unit: unit.ToString());
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var result = await client.GetFromJsonAsync<SampleWorkstationCapabilitiesResponse>("/api/workstations/WS-01/capabilities");
        Assert.Equal(unit, result!.TemplateVolumeUnit);
        Assert.Equal(0, vendor.RequestCount);
    }

    [Theory]
    [InlineData("Milliliters", true)]
    [InlineData("Microliters", false)]
    [InlineData(null, false)]
    [InlineData("9", false)]
    public async Task Milliliter_import_endpoint_requires_matching_explicit_file_unit(string? unit, bool accepted)
    {
        var template = SampleWorkstationTemplateFile.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "workstation-test-001.xlsx")));
        var file = SampleWorkstationTemplateFile.Write(template, SampleWorkstationTemplateVolumeUnit.Milliliters);
        using var vendor = new VendorHandler("")
        {
            ResponseFactory = request => request.Method == HttpMethod.Post ? """{"Code":200,"Data":"导入成功"}"""
                : JsonSerializer.Serialize(new { Code = 200, Data = new { FileName = "task.xlsx", FileData = Convert.ToBase64String(file) } })
        };
        await using var app = await CreateHostAsync(vendor, control: true, unit: "Milliliters");
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var content = new ByteArrayContent(file);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PostAsync("/api/workstations/WS-01/tasks/import?fileName=task.xlsx" + (unit is null ? "" : "&fileVolumeUnit=" + unit), content);
        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(accepted ? 2 : 0, vendor.RequestCount);
        if (accepted)
        {
            var result = (await response.Content.ReadFromJsonAsync<SampleWorkstationTaskImportResponse>())!;
            Assert.Equal(SampleWorkstationTemplateVolumeUnit.Milliliters, result.FileVolumeUnit);
            Assert.Equal(50, result.Template!.Transfers.Single().VolumeMicroliters);
        }
    }

    [Fact]
    public async Task Import_rejects_wrong_media_type_without_sending_a_command()
    {
        using var vendor = new VendorHandler("{}");
        await using var app = await CreateHostAsync(vendor, control: true);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsync("/api/workstations/WS-01/tasks/import?fileName=task.xlsx", new StringContent("not an xlsx"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, vendor.RequestCount);
    }

    [Fact]
    public async Task Import_http_endpoint_accepts_xlsx_and_returns_verified_task_without_start()
    {
        var file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "workstation-test-001.xlsx"));
        using var vendor = new VendorHandler("")
        {
            ResponseFactory = request => request.Method == HttpMethod.Post
                ? """{"Code":200,"Data":"导入成功"}"""
                : JsonSerializer.Serialize(new { Code = 200, Data = new { FileName = "task.xlsx", FileData = Convert.ToBase64String(file) } })
        };
        await using var app = await CreateHostAsync(vendor, control: true);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var content = new ByteArrayContent(file);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PostAsync("/api/workstations/WS-01/tasks/import?fileName=task.xlsx", content);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<SampleWorkstationTaskImportResponse>())!;
        Assert.True(result.ReadbackVerified);
        Assert.Equal("TEST-001", result.Template!.TaskNo);
        Assert.Equal(["/Service/ImportExperimentalTask", "/Service/GetExperimentalTaskTemplate"], vendor.Paths);
    }

    [Theory]
    [InlineData("TASK/01", false)]
    [InlineData("TASK%2F01", false)]
    [InlineData("任务 A&+?#/01", false)]
    [InlineData("TASK/01", true)]
    [InlineData("TASK%2F01", true)]
    [InlineData("任务 A&+?#/01", true)]
    public async Task V102_route_preserves_exact_task_number(string taskNo, bool start)
    {
        using var vendor = new VendorHandler(JsonSerializer.Serialize(new { Code = 200, Data = start ? "启动成功" : "更新成功" }));
        await using var app = await CreateHostAsync(vendor, control: true);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsJsonAsync($"/api/workstations/WS-01/tasks/{Uri.EscapeDataString(taskNo)}/{(start ? "start" : "barcodes")}",
            new SampleWorkstationTaskBarcodes { SampleBarcode1 = "A", SampleBarcode2 = "B" });
        response.EnsureSuccessStatusCode();
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(vendor.LastUri!.Query);
        Assert.Equal(taskNo, query["TaskNo"].ToString());
        Assert.Equal(1, vendor.RequestCount);
    }

    [Theory]
    [InlineData("start", "启动成功", "/Service/StartExperiment", true)]
    [InlineData("barcodes", "更新成功", "/Service/UpdateTaskBarcode", true)]
    [InlineData("barcodes", "更新任务信息完成!任务编号:'TASK-01',1号样品瓶条码:'来源+A&1',2号样品瓶条码:'B/2')", "/Service/UpdateTaskBarcode", true)]
    [InlineData("barcodes", "更新成功", "/Service/UpdateTaskBarcode", false)]
    public async Task V102_json_body_binds_both_barcodes_and_respects_control_policy(
        string action, string ack, string vendorPath, bool enabled)
    {
        using var vendor = new VendorHandler(JsonSerializer.Serialize(new { Code = 200, Data = ack }));
        await using var app = await CreateHostAsync(vendor, control: enabled);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsJsonAsync($"/api/workstations/WS-01/tasks/TASK-01/{action}",
            new SampleWorkstationTaskBarcodes { SampleBarcode1 = "来源+A&1", SampleBarcode2 = "B/2" });
        if (!enabled)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, vendor.RequestCount);
            return;
        }
        response.EnsureSuccessStatusCode();
        Assert.Equal(1, vendor.RequestCount);
        Assert.Equal(vendorPath, vendor.LastUri!.AbsolutePath);
        Assert.Equal(action == "barcodes" ? HttpMethod.Post : HttpMethod.Get, vendor.LastMethod);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(vendor.LastUri.Query);
        Assert.Equal("来源+A&1", query["SampleBarcode1"]);
        Assert.Equal("B/2", query["SampleBarcode2"]);
    }

    [Theory]
    [InlineData("{\"Code\":201,\"Data\":\"更新失败\"}", false, false)]
    [InlineData("{\"Code\":200,\"Data\":\"更新失败\"}", false, false)]
    [InlineData("{\"Code\":200,\"Data\":\"启动成功\"}", false, true)]
    [InlineData("", true, true)]
    public async Task Barcode_failure_preserves_known_rejection_or_unknown_outcome_without_start(
        string body, bool timeout, bool unknown)
    {
        using var vendor = new VendorHandler(body, timeout ? new TaskCanceledException("timeout") : null);
        await using var app = await CreateHostAsync(vendor, control: true);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsJsonAsync("/api/workstations/WS-01/tasks/TASK-01/barcodes",
            new SampleWorkstationTaskBarcodes { SampleBarcode1 = "A", SampleBarcode2 = "B" });
        Assert.Equal(timeout ? HttpStatusCode.GatewayTimeout : HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(unknown, problem.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Equal(1, vendor.RequestCount);
        Assert.Equal("/Service/UpdateTaskBarcode", vendor.LastUri!.AbsolutePath);
    }

    [Fact]
    public async Task Disabled_controls_and_unsupported_reads_never_reach_vendor()
    {
        using var vendor = new VendorHandler("""{"Code":200,"Data":"启动成功"}""");
        await using var app = await CreateHostAsync(vendor, control: false);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        foreach (var path in new[] { "initialize", "tasks/TASK-01/start" })
        {
            using var response = await client.PostAsync($"/api/workstations/WS-01/{path}", null);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(SampleWorkstationErrorCodes.ControlDisabled, problem.GetProperty("errorCode").GetString());
            Assert.False(problem.GetProperty("outcomeUnknown").GetBoolean());
        }
        using var unsupported = await client.GetAsync("/api/workstations/WS-01/protocol/WorkflowList");
        Assert.Equal(HttpStatusCode.NotImplemented, unsupported.StatusCode);
        var capabilities = await client.GetFromJsonAsync<SampleWorkstationCapabilitiesResponse>("/api/workstations/WS-01/capabilities");
        Assert.True(capabilities!.TaskImportSupported);
        Assert.Empty(capabilities.Commands);
        Assert.Equal(10, capabilities.ProtocolReads.Count);
        using var import = await client.PostAsync("/api/workstations/WS-01/tasks/import?fileName=task.xlsx", null);
        Assert.Equal(HttpStatusCode.Forbidden, import.StatusCode);
        Assert.Equal(0, vendor.RequestCount);
    }

    [Theory]
    [InlineData("{\"Code\":200,\"Data\":\"启动失败\"}", "workstation_command_unconfirmed", 200, false)]
    [InlineData("{\"Code\":202,\"Data\":\"vendor error\"}", "workstation_vendor_failure", 202, true)]
    [InlineData("", "workstation_invalid_payload", null, true)]
    public async Task Command_failures_preserve_reason_and_uncertainty_without_retries(
        string body,
        string code,
        int? vendorCode,
        bool outcomeUnknown)
    {
        using var vendor = new VendorHandler(body);
        await using var app = await CreateHostAsync(vendor, control: true);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsync("/api/workstations/WS-01/tasks/TASK-01/start", null);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("errorCode").GetString());
        Assert.Equal(outcomeUnknown, problem.GetProperty("outcomeUnknown").GetBoolean());
        if (vendorCode.HasValue)
            Assert.Equal(vendorCode.Value, problem.GetProperty("vendorCode").GetInt32());
        Assert.Equal(1, vendor.RequestCount);
    }

    [Fact]
    public async Task Successful_start_acknowledges_task_but_does_not_claim_completion()
    {
        using var vendor = new VendorHandler("""{"Code":200,"Data":"启动成功"}""");
        await using var app = await CreateHostAsync(vendor, control: true);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsync("/api/workstations/WS-01/tasks/TASK-01/start", null);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SampleWorkstationCommandResponse>();
        Assert.True(result!.Acknowledged);
        Assert.Equal("TASK-01", result.TaskNo);
        Assert.Equal("WS-01", result.DeviceId);
        Assert.Equal(HttpMethod.Get, vendor.LastMethod);
        Assert.Equal("/Service/StartExperiment?TaskNo=TASK-01", vendor.LastUri!.PathAndQuery);
        Assert.Equal(1, vendor.RequestCount);
    }

    [Fact]
    public async Task Disabled_device_still_exposes_configuration_but_cannot_read_or_control()
    {
        using var vendor = new VendorHandler("{}");
        await using var app = await CreateHostAsync(vendor, control: false, enabled: false);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var capabilities = await client.GetFromJsonAsync<SampleWorkstationCapabilitiesResponse>("/api/workstations/WS-01/capabilities");
        Assert.False(capabilities!.Enabled);
        Assert.Empty(capabilities.ProtocolReads);
        using var read = await client.GetAsync("/api/workstations/WS-01/status");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);
        using var missing = await client.GetAsync("/api/workstations/missing/capabilities");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(0, vendor.RequestCount);
    }

    [Theory]
    [InlineData(true, 504, "workstation_timeout")]
    [InlineData(false, 503, "workstation_unavailable")]
    public async Task Transport_failures_do_not_imply_command_rejection(bool timeout, int status, string code)
    {
        using var vendor = new VendorHandler("", timeout ? new TaskCanceledException("timeout") : new HttpRequestException("disconnect"));
        await using var app = await CreateHostAsync(vendor, control: true);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsync("/api/workstations/WS-01/initialize", null);
        Assert.Equal(status, (int)response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("errorCode").GetString());
        Assert.True(problem.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Equal(1, vendor.RequestCount);
    }

    private static async Task<WebApplication> CreateHostAsync(VendorHandler vendor, bool control, bool enabled = true, string unit = "Microliters")
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Devices:SampleWorkstation:DeviceId"] = "WS-01",
            ["Devices:SampleWorkstation:TemplateVolumeUnit"] = unit,
            ["Devices:SampleWorkstation:EquipmentNo"] = "EQ-01",
            // All requests are intercepted by VendorHandler; satisfy deployment URL validation.
            ["Devices:SampleWorkstation:BaseUrl"] = "http://127.0.0.1/Service/",
            ["Devices:SampleWorkstation:Enabled"] = enabled.ToString(),
            ["Devices:SampleWorkstation:ControlEnabled"] = control.ToString()
        }).Build();
        var context = new DeviceAdapterModuleContext(config, "Data Source=:memory:");
        var module = new SampleWorkstationAdapterModule();
        module.AddServices(builder.Services, context);
        builder.Services.AddHttpClient<VendorSampleWorkstationHttpClient>().ConfigurePrimaryHttpMessageHandler(() => vendor);
        builder.Services.AddSingleton(new DeviceOperationPolicy(new DeviceAdapterRegistry(module.GetDevices(context))));
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        module.MapEndpoints(app);
        await app.StartAsync();
        return app;
    }

    private sealed class VendorHandler(string body, Exception? exception = null) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? LastUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public Func<HttpRequestMessage, string>? ResponseFactory { get; init; }
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastUri = request.RequestUri;
            LastMethod = request.Method;
            Paths.Add(request.RequestUri!.AbsolutePath);
            return exception is not null ? Task.FromException<HttpResponseMessage>(exception)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ResponseFactory?.Invoke(request) ?? body, Encoding.UTF8, "application/octet-stream")
                });
        }
    }
}
