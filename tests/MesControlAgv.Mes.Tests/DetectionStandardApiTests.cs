using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Contracts.Materials;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Mes.Tests;

public sealed class DetectionStandardApiTests : IClassFixture<MesWebApplicationFactory>
{
    private readonly MesWebApplicationFactory _factory;

    public DetectionStandardApiTests(MesWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Revision_is_immutable_and_existing_assessment_uses_original_limits()
    {
        using var client = _factory.CreateClient();
        var sampleId = await AddSampleAsync();
        var code = $"GB/T METHOD-{Guid.NewGuid():N}";
        var first = await SaveStandardAsync(client, code, upper: 10);
        var result = await AssessAsync(client, sampleId, first, 10);
        Assert.Equal(DetectionVerdict.Pass, result.Verdict);

        var second = await SaveStandardAsync(client, code, upper: 5, baseRevisionId: first.RevisionId);
        Assert.Equal(2, second.Revision);
        Assert.NotEqual(first.RevisionId, second.RevisionId);
        var current = await client.GetFromJsonAsync<DetectionStandardRevision>(
            $"/api/materials/detection-standards/lookup?methodCode={Uri.EscapeDataString(code)}");
        Assert.Equal(second.RevisionId, current!.RevisionId);
        var old = await client.GetFromJsonAsync<DetectionStandardRevision>(
            $"/api/materials/detection-standards/lookup?methodCode={Uri.EscapeDataString(code)}&revision=1");
        Assert.False(old!.IsActive);
        Assert.Equal(10m, old.Indicators[0].UpperLimit);

        var history = await client.GetFromJsonAsync<List<DetectionAssessment>>(
            $"/api/materials/samples/{sampleId}/detection-assessments");
        Assert.Single(history!);
        Assert.Equal(first.RevisionId, history![0].RevisionId);
        Assert.Equal(10m, history[0].Items[0].UpperLimit);
        Assert.Equal(DetectionVerdict.Pass, history[0].Verdict);

        var stale = await client.PostAsJsonAsync("/api/materials/detection-standards", new SaveDetectionStandardRequest
        {
            BaseRevisionId = first.RevisionId,
            MethodCode = code,
            Name = "过期版本",
            Actor = "test-operator",
            Indicators = [new DetectionIndicatorRule { Name = "氯离子", Unit = "mg/L", UpperLimit = 1 }]
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var staleDeactivate = await client.PostAsJsonAsync(
            "/api/materials/detection-standards/deactivate", new DeactivateDetectionStandardRequest(code, first.RevisionId));
        Assert.Equal(HttpStatusCode.Conflict, staleDeactivate.StatusCode);
        var stillCurrent = await client.GetFromJsonAsync<DetectionStandardRevision>(
            $"/api/materials/detection-standards/lookup?methodCode={Uri.EscapeDataString(code)}");
        Assert.Equal(second.RevisionId, stillCurrent?.RevisionId);
    }

    [Theory]
    [InlineData("0.5", "Indeterminate", "低于检出限")]
    [InlineData("1", "Pass", "符合合格范围")]
    [InlineData("5", "Pass", "符合合格范围")]
    [InlineData("5.1", "Fail", "超出合格范围")]
    public async Task Detection_limit_and_inclusive_acceptance_limits_are_distinct(
        string valueText, string verdictText, string reason)
    {
        using var client = _factory.CreateClient();
        var sampleId = await AddSampleAsync();
        var standard = await SaveStandardAsync(client, $"METHOD-{Guid.NewGuid():N}", upper: 5, lower: 1);
        var result = await AssessAsync(client, sampleId, standard, decimal.Parse(valueText, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Enum.Parse<DetectionVerdict>(verdictText), result.Verdict);
        Assert.Equal(reason, result.Items[0].Reason);
    }

    [Fact]
    public async Task Request_replay_and_invalid_measurement_are_handled()
    {
        using var client = _factory.CreateClient();
        var sampleId = await AddSampleAsync();
        var standard = await SaveStandardAsync(client, $"METHOD-{Guid.NewGuid():N}", upper: 5);
        var request = Request(sampleId, standard, 4);

        var first = await client.PostAsJsonAsync("/api/materials/detection-assessments", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var replay = await client.PostAsJsonAsync("/api/materials/detection-assessments", request);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var different = await client.PostAsJsonAsync("/api/materials/detection-assessments", request with
        {
            Measurements = [request.Measurements[0] with { Value = 6 }]
        });
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);

        var wrongUnit = await client.PostAsJsonAsync("/api/materials/detection-assessments/preview", request with
        {
            RequestId = Guid.NewGuid(),
            Measurements = [request.Measurements[0] with { Unit = "ug/L" }]
        });
        Assert.Equal(HttpStatusCode.BadRequest, wrongUnit.StatusCode);

        var missingValue = await client.PostAsJsonAsync("/api/materials/detection-assessments", request with
        {
            RequestId = Guid.NewGuid(),
            Measurements = []
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingValue.StatusCode);
    }

    [Fact]
    public async Task Concurrent_identical_requests_keep_one_assessment()
    {
        using var client = _factory.CreateClient();
        var sampleId = await AddSampleAsync();
        var standard = await SaveStandardAsync(client, $"METHOD-{Guid.NewGuid():N}", upper: 5);
        var request = Request(sampleId, standard, 4);

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync("/api/materials/detection-assessments", request),
            client.PostAsJsonAsync("/api/materials/detection-assessments", request));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var records = await client.GetFromJsonAsync<List<DetectionAssessment>>(
            $"/api/materials/samples/{sampleId}/detection-assessments");
        Assert.Single(records!);
    }

    [Fact]
    public async Task Existing_database_gets_detection_tables_on_startup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"detection-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<MesDbContext>().UseSqlite($"Data Source={path}").Options;
            await using (var database = new MesDbContext(options))
            {
                await database.Database.EnsureCreatedAsync();
                await database.Database.ExecuteSqlRawAsync("DROP TABLE DetectionAssessmentItems;");
                await database.Database.ExecuteSqlRawAsync("DROP TABLE DetectionAssessments;");
                await database.Database.ExecuteSqlRawAsync("DROP TABLE DetectionIndicatorRules;");
                await database.Database.ExecuteSqlRawAsync("DROP TABLE DetectionStandardRevisions;");
            }

            using var factory = new MesWebApplicationFactory(path);
            using var client = factory.CreateClient();
            var standards = await client.GetFromJsonAsync<List<DetectionStandardRevision>>(
                "/api/materials/detection-standards");
            Assert.Empty(standards!);
            using var scope = factory.Services.CreateScope();
            var databaseAfterUpgrade = scope.ServiceProvider.GetRequiredService<MesDbContext>();
            Assert.Empty(await databaseAfterUpgrade.DetectionAssessments.ToListAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Deactivated_standard_cannot_create_new_assessment_and_history_remains()
    {
        using var client = _factory.CreateClient();
        var sampleId = await AddSampleAsync();
        var standard = await SaveStandardAsync(client, $"METHOD-{Guid.NewGuid():N}", upper: null);
        var result = await AssessAsync(client, sampleId, standard, 3);
        Assert.Equal(DetectionVerdict.Indeterminate, result.Verdict);
        Assert.Equal("未配置合格限值", result.Items[0].Reason);

        var stopped = await client.PostAsJsonAsync(
            "/api/materials/detection-standards/deactivate", new DeactivateDetectionStandardRequest(standard.MethodCode, standard.RevisionId));
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        var rejected = await client.PostAsJsonAsync("/api/materials/detection-assessments", Request(sampleId, standard, 3));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var history = await client.GetFromJsonAsync<List<DetectionAssessment>>(
            $"/api/materials/samples/{sampleId}/detection-assessments");
        Assert.Single(history!);
    }

    private async Task<Guid> AddSampleAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        var id = Guid.NewGuid();
        database.SampleMaterials.Add(new SampleMaterialRecord
        {
            SampleId = id,
            Barcode = $"DETECT-{id:N}",
            SampleBatchId = $"BATCH-{id:N}",
            Status = "Registered",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await database.SaveChangesAsync();
        return id;
    }

    private static async Task<DetectionStandardRevision> SaveStandardAsync(
        HttpClient client, string code, decimal? upper, decimal? lower = null, Guid? baseRevisionId = null)
    {
        var response = await client.PostAsJsonAsync("/api/materials/detection-standards", new SaveDetectionStandardRequest
        {
            BaseRevisionId = baseRevisionId,
            MethodCode = code,
            Name = "水样氯离子",
            Category = "水",
            Actor = "test-operator",
            Indicators = [new DetectionIndicatorRule
            {
                Name = "氯离子", Unit = "mg/L", DetectionLimit = 1,
                LowerLimit = lower, UpperLimit = upper
            }]
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DetectionStandardRevision>())!;
    }

    private static DetectionAssessmentRequest Request(Guid sampleId, DetectionStandardRevision standard, decimal value) => new()
    {
        RequestId = Guid.NewGuid(),
        SampleId = sampleId,
        RevisionId = standard.RevisionId,
        Actor = "test-operator",
        Measurements = [new DetectionMeasurement
        {
            IndicatorId = standard.Indicators[0].IndicatorId,
            Value = value,
            Unit = "mg/L"
        }]
    };

    private static async Task<DetectionAssessment> AssessAsync(
        HttpClient client, Guid sampleId, DetectionStandardRevision standard, decimal value)
    {
        var response = await client.PostAsJsonAsync("/api/materials/detection-assessments", Request(sampleId, standard, value));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DetectionAssessment>())!;
    }
}
