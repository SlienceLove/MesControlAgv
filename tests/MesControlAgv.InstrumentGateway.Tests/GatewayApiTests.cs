using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MesControlAgv.InstrumentGateway.Tests;

public sealed class GatewayApiTests : IClassFixture<DisabledGatewayFactory>
{
    private readonly HttpClient _client;

    public GatewayApiTests(DisabledGatewayFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Health_ReportsDisabledReadOnlyModeWithoutOpeningSerialPort()
    {
        var response = await _client.GetAsync("/health");
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("disabled", body.Status);
        Assert.Equal("read-only", body.Mode);
        Assert.Equal("COM-NOT-PRESENT", body.ComPort);
    }

    [Fact]
    public async Task HeadHealth_IsAllowed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, "/health");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task InstrumentEndpoint_ReturnsServiceUnavailableWhileDisabled()
    {
        var response = await _client.GetAsync("/api/instruments/CIC-D160-01/status");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task StateChangingMethods_AreRejected()
    {
        var response = await _client.PostAsJsonAsync("/health", new { value = "ignored" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("read-only", body);
    }

    private sealed record HealthResponse(string Status, string Mode, string ComPort);
}

public sealed class DisabledGatewayFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IonChromatography:Enabled"] = "false",
                ["IonChromatography:ComPort"] = "COM-NOT-PRESENT"
            });
        });
    }
}
