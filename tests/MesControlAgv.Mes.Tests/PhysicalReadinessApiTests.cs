using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Contracts;

namespace MesControlAgv.Mes.Tests;

public sealed class PhysicalReadinessApiTests : IClassFixture<MesWebApplicationFactory>
{
    private readonly HttpClient _client;

    public PhysicalReadinessApiTests(MesWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Readiness_endpoint_is_safe_and_disabled_by_default()
    {
        var response = await _client.GetAsync("/api/physical/readiness");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PhysicalReadinessResponse>();
        Assert.NotNull(result);
        Assert.False(result!.Enabled);
        Assert.True(result.ReadOnly);
        Assert.False(result.SchedulingPermitted);
        Assert.Contains(
            PhysicalReadinessReasonCodes.SupervisorDisabled,
            result.BlockingReasons);
    }

    [Fact]
    public async Task Manual_refresh_without_a_body_remains_read_only()
    {
        using var response = await _client.PostAsync(
            "/api/physical/readiness/refresh",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PhysicalReadinessResponse>();
        Assert.NotNull(result);
        Assert.True(result!.ReadOnly);
        Assert.False(result.Enabled);
    }
}
