using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Contracts.Materials;
using MesControlAgv.Mes.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MesControlAgv.Mes.Tests;

public sealed class SampleBatchDetectionMethodTests : IClassFixture<MesWebApplicationFactory>
{
    private readonly MesWebApplicationFactory _factory;

    public SampleBatchDetectionMethodTests(MesWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task New_sample_batch_method_is_inherited_by_other_samples()
    {
        using var client = _factory.CreateClient();
        var standard = await AddStandardAsync(client);
        var batch = $"BATCH-{Guid.NewGuid():N}";
        var firstBarcode = $"S-{Guid.NewGuid():N}";
        var secondBarcode = $"S-{Guid.NewGuid():N}";
        var import = Request(batch,
            new SampleImportRow { RowNumber = 1, Barcode = firstBarcode, SampleBatchId = batch,
                DetectionMethodCode = standard.MethodCode, LocationCode = "DEFAULT" },
            new SampleImportRow { RowNumber = 2, Barcode = secondBarcode, SampleBatchId = batch,
                LocationCode = "DEFAULT" });

        var preview = await client.PostAsJsonAsync("/api/materials/import/preview", import);
        Assert.True((await preview.Content.ReadFromJsonAsync<MaterialImportPreview>())!.CanCommit);
        var response = await client.PostAsJsonAsync("/api/materials/import", import);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var laterBarcode = $"S-{Guid.NewGuid():N}";
        var later = Request(batch, new SampleImportRow
        {
            RowNumber = 1, Barcode = laterBarcode, SampleBatchId = batch, LocationCode = "DEFAULT"
        });
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/materials/import", later)).StatusCode);
        var samples = await client.GetFromJsonAsync<List<SampleMaterial>>("/api/materials/samples");
        var batchSamples = samples!.Where(item => string.Equals(item.SampleBatchId, batch, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(3, batchSamples.Count);
        Assert.All(batchSamples, item => Assert.Equal(standard.MethodCode, item.DetectionMethodCode));
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MesDbContext>();
        Assert.Single(await database.SampleBatchDetectionMethods.Where(item => item.SampleBatchId == batch.ToUpperInvariant()).ToListAsync());
    }

    [Fact]
    public async Task Conflicting_method_in_same_or_existing_batch_is_rejected()
    {
        using var client = _factory.CreateClient();
        var first = await AddStandardAsync(client);
        var second = await AddStandardAsync(client);
        var batch = $"BATCH-{Guid.NewGuid():N}";
        var withinRequest = Request(batch,
            new SampleImportRow { RowNumber = 1, Barcode = $"S-{Guid.NewGuid():N}", SampleBatchId = batch,
                DetectionMethodCode = first.MethodCode, LocationCode = "DEFAULT" },
            new SampleImportRow { RowNumber = 2, Barcode = $"S-{Guid.NewGuid():N}", SampleBatchId = batch,
                DetectionMethodCode = second.MethodCode, LocationCode = "DEFAULT" });
        var conflictPreview = await client.PostAsJsonAsync("/api/materials/import/preview", withinRequest);
        var issues = await conflictPreview.Content.ReadFromJsonAsync<MaterialImportPreview>();
        Assert.Contains(issues!.Issues, issue => issue.Field == nameof(SampleImportRow.DetectionMethodCode));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync("/api/materials/import", withinRequest)).StatusCode);

        var accepted = Request(batch, withinRequest.Samples[0]);
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/materials/import", accepted)).StatusCode);
        var later = Request(batch, new SampleImportRow
        {
            RowNumber = 1, Barcode = $"S-{Guid.NewGuid():N}", SampleBatchId = batch,
            DetectionMethodCode = second.MethodCode, LocationCode = "DEFAULT"
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync("/api/materials/import", later)).StatusCode);
    }

    [Fact]
    public async Task Bound_batch_rejects_other_assessment_method_but_accepts_new_revision()
    {
        using var client = _factory.CreateClient();
        var first = await AddStandardAsync(client);
        var other = await AddStandardAsync(client);
        var batch = $"BATCH-{Guid.NewGuid():N}";
        var barcode = $"S-{Guid.NewGuid():N}";
        var import = Request(batch, new SampleImportRow
        {
            RowNumber = 1, Barcode = barcode, SampleBatchId = batch,
            DetectionMethodCode = first.MethodCode, LocationCode = "DEFAULT"
        });
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/materials/import", import)).StatusCode);
        var sample = (await client.GetFromJsonAsync<List<SampleMaterial>>("/api/materials/samples"))!
            .Single(item => string.Equals(item.Barcode, barcode, StringComparison.OrdinalIgnoreCase));

        var rejected = await client.PostAsJsonAsync("/api/materials/detection-assessments/preview",
            Assessment(sample.SampleId, other));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        var next = new SaveDetectionStandardRequest
        {
            BaseRevisionId = first.RevisionId, MethodCode = first.MethodCode,
            Name = "升级方法", Actor = "test-operator",
            Indicators = [new DetectionIndicatorRule { Name = "氯离子", Unit = "mg/L", UpperLimit = 8 }]
        };
        var revisionResponse = await client.PostAsJsonAsync("/api/materials/detection-standards", next);
        Assert.Equal(HttpStatusCode.Created, revisionResponse.StatusCode);
        var upgraded = (await revisionResponse.Content.ReadFromJsonAsync<DetectionStandardRevision>())!;
        var accepted = await client.PostAsJsonAsync("/api/materials/detection-assessments", Assessment(sample.SampleId, upgraded));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var refreshed = (await client.GetFromJsonAsync<List<SampleMaterial>>("/api/materials/samples"))!
            .Single(item => item.SampleId == sample.SampleId);
        Assert.Equal(first.MethodCode, refreshed.DetectionMethodCode);
    }

    [Fact]
    public async Task Legacy_sample_import_without_method_remains_unbound()
    {
        using var client = _factory.CreateClient();
        var batch = $"BATCH-{Guid.NewGuid():N}";
        var barcode = $"S-{Guid.NewGuid():N}";
        var import = Request(batch, new SampleImportRow
        {
            RowNumber = 1, Barcode = barcode, SampleBatchId = batch, LocationCode = "DEFAULT"
        });
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/materials/import", import)).StatusCode);
        var sample = (await client.GetFromJsonAsync<List<SampleMaterial>>("/api/materials/samples"))!
            .Single(item => string.Equals(item.Barcode, barcode, StringComparison.OrdinalIgnoreCase));
        Assert.Null(sample.DetectionMethodCode);
    }

    [Fact]
    public async Task Inactive_method_cannot_be_assigned_to_new_batch()
    {
        using var client = _factory.CreateClient();
        var standard = await AddStandardAsync(client);
        var stopped = await client.PostAsJsonAsync("/api/materials/detection-standards/deactivate",
            new DeactivateDetectionStandardRequest(standard.MethodCode, standard.RevisionId));
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        var batch = $"BATCH-{Guid.NewGuid():N}";
        var request = Request(batch, new SampleImportRow
        {
            RowNumber = 1, Barcode = $"S-{Guid.NewGuid():N}", SampleBatchId = batch,
            DetectionMethodCode = standard.MethodCode, LocationCode = "DEFAULT"
        });
        var preview = await client.PostAsJsonAsync("/api/materials/import/preview", request);
        var result = await preview.Content.ReadFromJsonAsync<MaterialImportPreview>();
        Assert.Contains(result!.Issues, issue => issue.Field == nameof(SampleImportRow.DetectionMethodCode));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync("/api/materials/import", request)).StatusCode);
    }

    [Fact]
    public async Task Accepted_import_replays_after_its_method_is_deactivated()
    {
        using var client = _factory.CreateClient();
        var standard = await AddStandardAsync(client);
        var batch = $"BATCH-{Guid.NewGuid():N}";
        var request = Request(batch, new SampleImportRow
        {
            RowNumber = 1, Barcode = $"S-{Guid.NewGuid():N}", SampleBatchId = batch,
            DetectionMethodCode = standard.MethodCode, LocationCode = "DEFAULT"
        });
        var first = await client.PostAsJsonAsync("/api/materials/import", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var stopped = await client.PostAsJsonAsync("/api/materials/detection-standards/deactivate",
            new DeactivateDetectionStandardRequest(standard.MethodCode, standard.RevisionId));
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);

        var replay = await client.PostAsJsonAsync("/api/materials/import", request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await replay.Content.ReadFromJsonAsync<MaterialCommandResult<MaterialImportResult>>())!
            .IsIdempotentReplay);

        var inherited = Request(batch, new SampleImportRow
        {
            RowNumber = 1, Barcode = $"S-{Guid.NewGuid():N}", SampleBatchId = batch,
            LocationCode = "DEFAULT"
        });
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/materials/import", inherited)).StatusCode);
    }

    private static MaterialImportRequest Request(string batch, params SampleImportRow[] rows) => new()
    {
        RequestId = Guid.NewGuid(), Actor = "test-operator", Source = batch, Samples = rows
    };

    private static DetectionAssessmentRequest Assessment(Guid sampleId, DetectionStandardRevision standard) => new()
    {
        RequestId = Guid.NewGuid(), SampleId = sampleId, RevisionId = standard.RevisionId,
        Actor = "test-operator",
        Measurements = [new DetectionMeasurement
        {
            IndicatorId = standard.Indicators[0].IndicatorId, Value = 4, Unit = "mg/L"
        }]
    };

    private static async Task<DetectionStandardRevision> AddStandardAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/materials/detection-standards",
            new SaveDetectionStandardRequest
            {
                MethodCode = $"M-{Guid.NewGuid():N}", Name = "测试方法", Actor = "test-operator",
                Indicators = [new DetectionIndicatorRule { Name = "氯离子", Unit = "mg/L", UpperLimit = 10 }]
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DetectionStandardRevision>())!;
    }
}
