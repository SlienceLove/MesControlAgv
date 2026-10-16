using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Mes.Tests;

public sealed class LabReportTests : IClassFixture<MesWebApplicationFactory>
{
    private readonly MesWebApplicationFactory _factory;
    public LabReportTests(MesWebApplicationFactory factory) => _factory = factory;

    [Fact] public async Task SnapshotIsReadOnlyAndFiltersHalfOpenUtcRangeAndBatch()
    {
        var batch = Guid.NewGuid().ToString("N"); var from = new DateTime(2026, 9, 20, 16, 0, 0, DateTimeKind.Utc);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            foreach (var time in new[] { from.AddTicks(-1), from, from.AddHours(24) })
                db.ExperimentJobs.Add(new ExperimentJobRecord { JobId = Guid.NewGuid(), SampleBatchId = batch,
                    Status = "Completed", SampleId = "00001", CreatedAtUtc = time, UpdatedAtUtc = time });
            await db.SaveChangesAsync();
        }
        using var client = _factory.CreateClient();
        var snapshot = await client.GetFromJsonAsync<ReportSnapshot>(
            $"/api/lab-reports/snapshot?from=2026-09-21T00:00:00%2B08:00&to=2026-09-22T00:00:00%2B08:00&batch={batch}");
        Assert.Single(snapshot!.Jobs); Assert.Equal("00001", snapshot.Jobs[0].SampleId);
        Assert.Empty(snapshot.Measurements); Assert.Null(ReportRules.Calculate(snapshot.Jobs).AverageSeconds);
        using var after = _factory.Services.CreateScope();
        Assert.Equal(3, after.ServiceProvider.GetRequiredService<MesDbContext>().ExperimentJobs.Count(x => x.SampleBatchId == batch));
    }
    [Fact] public async Task InvalidRangeReturnsBadRequest()
    {
        using var client = _factory.CreateClient();
        using var result = await client.GetAsync("/api/lab-reports/snapshot?from=2026-09-22T00:00:00Z&to=2026-09-21T00:00:00Z");
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }
    [Fact] public async Task EmptySummaryDoesNotCallModel()
    {
        using var client = _factory.CreateClient();
        using var result = await client.PostAsJsonAsync("/api/lab-reports/summary",
            ReportRules.Empty(new(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow)));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.False((await result.Content.ReadFromJsonAsync<AiSummary>())!.Available);
    }
}
