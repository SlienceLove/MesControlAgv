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

    private static async Task<WebApplication> CreateHostAsync(VendorHandler vendor, bool control, bool enabled = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Devices:SampleWorkstation:DeviceId"] = "WS-01",
            ["Devices:SampleWorkstation:EquipmentNo"] = "EQ-01",
            ["Devices:SampleWorkstation:BaseUrl"] = "http://vendor.invalid/Service/",
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
