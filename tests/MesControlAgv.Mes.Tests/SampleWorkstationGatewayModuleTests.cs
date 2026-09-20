using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Endpoints;
using MesControlAgv.Mes.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MesControlAgv.Mes.Tests;

public sealed class SampleWorkstationGatewayModuleTests
{
    [Theory]
    [InlineData(403, "workstation_control_disabled", false)]
    [InlineData(501, "workstation_unsupported_operation", false)]
    [InlineData(502, "workstation_command_unconfirmed", true)]
    [InlineData(504, "workstation_timeout", true)]
    public async Task Standalone_module_preserves_structured_adapter_failures(int status, string code, bool unknown)
    {
        var payload = JsonSerializer.Serialize(new
        {
            detail = "启动失败", errorCode = code, outcomeUnknown = unknown,
            vendorCode = 200, vendorData = "启动失败"
        });
        using var adapter = new AdapterHandler(status, payload);
        await using var app = await CreateHostAsync(adapter);
        using var client = app.GetTestClient();
        using var response = await client.PostAsync("/api/workstations/WS-01/tasks/TASK-01/start", null);
        Assert.Equal(status, (int)response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, result.GetProperty("errorCode").GetString());
        Assert.Equal(unknown, result.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Equal("启动失败", result.GetProperty("vendorData").GetString());
        Assert.Equal(200, result.GetProperty("vendorCode").GetInt32());
        Assert.Equal(1, adapter.Calls);
        Assert.Equal(HttpMethod.Post, adapter.LastMethod);
    }

    [Fact]
    public async Task Standalone_gateway_registers_ports_and_proxies_capabilities_without_vendor_address()
    {
        var capabilities = new SampleWorkstationCapabilitiesResponse("WS-01", true, false, false, [],
            [SampleWorkstationProtocolOperation.SolventParameterList]);
        using var adapter = new AdapterHandler(200, JsonSerializer.Serialize(capabilities));
        await using var app = await CreateHostAsync(adapter);
        using var client = app.GetTestClient();
        var result = await client.GetFromJsonAsync<SampleWorkstationCapabilitiesResponse>("/api/workstations/WS-01/capabilities");
        Assert.Equal("AdapterConfiguration", result!.Source);
        Assert.False(result.TaskImportSupported);
        Assert.Contains(SampleWorkstationProtocolOperation.SolventParameterList, result.ProtocolReads);
        Assert.Equal("selected-adapter.invalid", adapter.LastUri!.Host);
        Assert.Equal("/api/workstations/WS-01/capabilities", adapter.LastUri.AbsolutePath);
        using var scope = app.Services.CreateScope();
        Assert.IsType<SampleWorkstationAdapterClient>(scope.ServiceProvider.GetRequiredService<ISampleWorkstationReader>());
        Assert.IsType<SampleWorkstationAdapterClient>(scope.ServiceProvider.GetRequiredService<ISampleWorkstationCommands>());
        Assert.IsType<SampleWorkstationAdapterClient>(scope.ServiceProvider.GetRequiredService<ISampleWorkstationTaskImporter>());
    }

    [Fact]
    public async Task Start_returns_acknowledgement_and_task_number_through_standalone_gateway()
    {
        using var data = JsonDocument.Parse("\"启动成功\"");
        var ack = new SampleWorkstationCommandResponse("WS-01", SampleWorkstationCommandOperation.StartTask,
            200, data.RootElement, DateTimeOffset.UtcNow) { Acknowledged = true, TaskNo = "TASK-01" };
        using var adapter = new AdapterHandler(200, JsonSerializer.Serialize(ack));
        await using var app = await CreateHostAsync(adapter);
        using var client = app.GetTestClient();
        using var response = await client.PostAsync("/api/workstations/WS-01/tasks/TASK-01/start", null);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SampleWorkstationCommandResponse>();
        Assert.True(result!.Acknowledged);
        Assert.Equal("TASK-01", result.TaskNo);
        Assert.Equal(1, adapter.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_send_another_command()
    {
        using var adapter = new AdapterHandler(504, "{}");
        using var http = new HttpClient(adapter) { BaseAddress = new Uri("http://adapter.invalid/") };
        var gateway = new SampleWorkstationAdapterClient(http);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gateway.StartTaskAsync("WS-01", "TASK-01", cancellation.Token));
        Assert.InRange(adapter.Calls, 0, 1);
    }

    private static async Task<WebApplication> CreateHostAsync(AdapterHandler adapter)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Adapter:BaseUrl"] = "http://generic-adapter.invalid/",
            ["SampleWorkstationGateway:AdapterBaseUrl"] = "http://selected-adapter.invalid/"
        }).Build();
        builder.Services.AddSampleWorkstationGateway(configuration);
        builder.Services.AddHttpClient<SampleWorkstationAdapterClient>().ConfigurePrimaryHttpMessageHandler(() => adapter);
        var app = builder.Build();
        app.MapSampleWorkstationEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class AdapterHandler(int status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastUri = request.RequestUri;
            LastMethod = request.Method;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
