using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Contracts.Samples;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Mes.Tests;

public sealed class SampleManagementApiTests(MesWebApplicationFactory factory)
    : IClassFixture<MesWebApplicationFactory>
{
    [Fact]
    public async Task Register_bind_move_and_events_are_traceable()
    {
        using var client = factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sampleId = $"SAMPLE-{suffix}";
        var runId = await CreateRunAsync(client, $"BATCH-{suffix}");

        var registeredResponse = await client.PostAsJsonAsync("/api/samples", new RegisterSampleRequest
        {
            SampleId = sampleId,
            Barcode = $"BC-{suffix}",
            SampleBatchId = $"BATCH-{suffix}",
            SourceLocation = "WH-A-01",
            ContainerPosition = "A1",
            OperatorName = "tester"
        });
        Assert.Equal(HttpStatusCode.Created, registeredResponse.StatusCode);

        var bound = await client.PostAsJsonAsync(
            $"/api/samples/{sampleId}/bind-run",
            new BindSampleRunRequest { RunId = runId, OperatorName = "tester" });
        bound.EnsureSuccessStatusCode();

        var moved = await client.PostAsJsonAsync(
            $"/api/samples/{sampleId}/moves",
            new MoveSampleRequest
            {
                OperationId = Guid.NewGuid(),
                DeviceId = "AGV-01",
                ToLocation = "WORKSTATION-01",
                OperatorName = "tester"
            });
        moved.EnsureSuccessStatusCode();
        var snapshot = await moved.Content.ReadFromJsonAsync<SampleRecordResponse>();
        Assert.Equal(runId, snapshot!.RunId);
        Assert.Equal(SampleLifecycleStatus.InTransit, snapshot.Status);
        Assert.Equal("WORKSTATION-01", snapshot.CurrentLocation);

        var events = await client.GetFromJsonAsync<IReadOnlyList<SampleEventResponse>>(
            $"/api/samples/{sampleId}/events");
        Assert.Equal(3, events!.Count);
        Assert.Equal("Registered", events[0].EventType);
        Assert.Equal("RunBound", events[1].EventType);
        Assert.Equal("DeviceMove", events[2].EventType);
    }

    [Fact]
    public async Task Import_is_row_level_idempotent_and_reports_bad_rows()
    {
        using var client = factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sampleId = $"IMPORT-{suffix}";
        var runId = await CreateRunAsync(client, $"BATCH-{suffix}");

        var request = new ImportSamplesRequest
        {
            SourceFileName = "sample-template.xlsx",
            OperatorName = "tester",
            Rows =
            [
                new SampleImportRowRequest
                {
                    RowNumber = 2,
                    SampleId = sampleId,
                    Barcode = $"BC-{suffix}",
                    SampleBatchId = $"BATCH-{suffix}",
                    SourceLocation = "WH-A-02",
                    RunId = runId
                },
                new SampleImportRowRequest
                {
                    RowNumber = 3,
                    SampleId = sampleId,
                    Barcode = $"BC-{suffix}",
                    SampleBatchId = $"BATCH-{suffix}",
                    SourceLocation = "WH-A-02"
                },
                new SampleImportRowRequest
                {
                    RowNumber = 4,
                    SampleId = "bad sample id",
                    Barcode = "BC-BAD",
                    SampleBatchId = "BATCH-BAD",
                    SourceLocation = "WH-A-03"
                }
            ]
        };

        var response = await client.PostAsJsonAsync("/api/samples/import", request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ImportSamplesResponse>();

        Assert.Equal(3, result!.TotalRows);
        Assert.Equal(2, result.SucceededRows);
        Assert.Equal(1, result.ExistingRows);
        Assert.Equal(1, result.FailedRows);
        Assert.Equal("created", result.Rows[0].Outcome);
        Assert.Equal("existing", result.Rows[1].Outcome);
        Assert.Equal("failed", result.Rows[2].Outcome);
        Assert.Contains("bounded", result.Rows[2].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(runId, result.Rows[0].Sample!.RunId);

        var listed = await client.GetFromJsonAsync<IReadOnlyList<SampleRecordResponse>>(
            "/api/samples?limit=10");
        Assert.Contains(listed!, item =>
            string.Equals(item.SampleId, sampleId, StringComparison.OrdinalIgnoreCase) && item.RunId == runId);
    }

    [Fact]
    public async Task Device_move_requires_a_bound_run()
    {
        using var client = factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sampleId = $"UNBOUND-{suffix}";

        var registeredResponse = await client.PostAsJsonAsync("/api/samples", new RegisterSampleRequest
        {
            SampleId = sampleId,
            Barcode = $"BC-{suffix}",
            SampleBatchId = "BATCH-UNBOUND",
            SourceLocation = "WH-A-01",
            OperatorName = "tester"
        });
        registeredResponse.EnsureSuccessStatusCode();

        var invalidBinding = await client.PostAsJsonAsync(
            $"/api/samples/{sampleId}/bind-run",
            new BindSampleRunRequest { RunId = Guid.NewGuid(), OperatorName = "tester" });
        Assert.Equal(HttpStatusCode.Conflict, invalidBinding.StatusCode);

        var moved = await client.PostAsJsonAsync(
            $"/api/samples/{sampleId}/moves",
            new MoveSampleRequest
            {
                OperationId = Guid.NewGuid(),
                DeviceId = "AGV-01",
                ToLocation = "WORKSTATION-01",
                OperatorName = "tester"
            });
        Assert.Equal(HttpStatusCode.Conflict, moved.StatusCode);
    }

    // Sample binding now validates a durable run and its batch. Create that
    // record through the in-process API; DryRun never dispatches a device.
    private static async Task<Guid> CreateRunAsync(HttpClient client, string batchId)
    {
        var create = await client.PostAsJsonAsync("/api/workflows?actor=tester",
            WorkflowTestDefinitions.CreateTimedWaitWorkflow("1"));
        create.EnsureSuccessStatusCode();
        var draft = (await create.Content.ReadFromJsonAsync<WorkflowVersion>())!;
        var validate = await client.PostAsync($"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/validate", null);
        validate.EnsureSuccessStatusCode();
        var publish = await client.PostAsync($"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/publish?actor=tester", null);
        publish.EnsureSuccessStatusCode();
        var execute = await client.PostAsJsonAsync("/api/workflows/execute", new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId, Version = draft.Version, RequestId = Guid.NewGuid(),
            RequestedBy = "tester", DryRun = true,
            Parameters = new Dictionary<string, string?> { [WorkflowRuntimeParameterNames.SampleBatchId] = batchId }
        });
        execute.EnsureSuccessStatusCode();
        var result = (await execute.Content.ReadFromJsonAsync<WorkflowExecutionResult>())!;
        Assert.True(result.IsAccepted, result.RejectionReason);
        return result.ExecutionId;
    }
}
