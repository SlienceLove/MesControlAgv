using System.Net.Http.Json;
using MesControlAgv.Contracts;

namespace MesControlAgv.Mes.Tests;

public sealed class KpiApiTests : IClassFixture<MesWebApplicationFactory>
{
    private readonly HttpClient _client;

    public KpiApiTests(MesWebApplicationFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Kpi_endpoint_returns_today_task_summary_and_24_hour_trend()
    {
        var create = await _client.PostAsJsonAsync("/api/tasks", new
        {
            sourceStationCode = 2,
            targetStationCode = 4,
            priority = 3,
            description = "KPI test"
        });
        create.EnsureSuccessStatusCode();

        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        var dashboard = await _client.GetFromJsonAsync<KpiDashboardResponse>($"/api/dashboard/kpi?date={date:yyyy-MM-dd}");

        Assert.NotNull(dashboard);
        Assert.Equal(date, dashboard.Date);
        Assert.True(dashboard.TaskSummary.Total >= 1);
        Assert.Equal(24, dashboard.TaskTrend.Count);
        Assert.NotEmpty(dashboard.SampleSummary.DataSource);
        Assert.NotEmpty(dashboard.Consumables);
        Assert.NotEmpty(dashboard.Instruments);
    }
}
