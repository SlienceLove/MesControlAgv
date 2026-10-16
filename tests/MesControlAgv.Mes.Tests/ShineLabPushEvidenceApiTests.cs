using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Tests;

public sealed class ShineLabPushEvidenceApiTests
{
    [Fact]
    public async Task Existing_database_gains_evidence_table_and_api_returns_persisted_unmatched_frames()
    {
        string path;
        using (var previous = new MesWebApplicationFactory())
        {
            using var client = previous.CreateClient();
            path = previous.DatabasePath;
            using var scope = previous.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            // Simulate the schema of the previous application on this test-only
            // temporary database. No field database is accessed.
            await db.Database.ExecuteSqlRawAsync("DROP TABLE ShineLabPushEvidence;");
            var repository = new ShineLabTaskRepository(db);
            await repository.CreateOrGetAsync(new("STN61_01", "preserved-task",
                [new("6", "sample", "0", 1, "1", "")]), default);
        }
        using var upgraded = new MesWebApplicationFactory(path);
        using var upgradedClient = upgraded.CreateClient();
        using (var scope = upgraded.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            Assert.True(await db.ShineLabTasks.AnyAsync(task => task.TaskUuid == "preserved-task"));
            var options = Options.Create(new ShineLabTcpOptions { PayloadProfile = ShineLabPayloadProfile.DcSeqSingleChannel });
            var repository = new ShineLabTaskRepository(db, options);
            var body = JsonSerializer.Deserialize<JsonElement>("""{"sampleId":"6","finishDate":"2026-09-21 16:51:42"}""");
            await repository.ApplyPushAsync("SampleFinish", "STN61_01", body, default, "first");
            await repository.ApplyPushAsync("SampleFinish", "STN61_01", body, default, "second");
        }
        var evidence = await upgradedClient.GetFromJsonAsync<List<ShineLabPushEvidence>>(
            "/api/shinelab/devices/STN61_01/push-evidence?limit=10");
        Assert.NotNull(evidence);
        Assert.Equal(2, evidence.Count);
        Assert.Single(evidence.Where(item => item.IsDuplicate));
        Assert.All(evidence, item => Assert.Equal("MissingTaskId", item.Correlation));
        var limited = await upgradedClient.GetFromJsonAsync<List<ShineLabPushEvidence>>(
            "/api/shinelab/devices/STN61_01/push-evidence?limit=1");
        Assert.Single(limited!);
        Assert.Equal("second", limited![0].StrId);
    }
}
