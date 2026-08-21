using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Workflows;

namespace MesControlAgv.Mes.Tests;

public sealed class WorkflowApiTests : IClassFixture<MesWebApplicationFactory>
{
    private readonly HttpClient _client;

    public WorkflowApiTests(MesWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Workflow_endpoints_create_validate_publish_read_and_admit_execution()
    {
        var definition = WorkflowTestDefinitions.CreateMoveWorkflow();
        var preview = await _client.PostAsJsonAsync("/api/workflows/validate", definition);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewResult = await preview.Content.ReadFromJsonAsync<WorkflowValidationResult>();
        Assert.NotNull(previewResult);

        var create = await _client.PostAsJsonAsync("/api/workflows?actor=planner-api", definition);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var draft = await create.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(draft);

        var validation = await _client.PostAsync(
            $"/api/workflows/{draft!.WorkflowId}/versions/{draft.Version}/validate",
            content: null);
        Assert.Equal(HttpStatusCode.OK, validation.StatusCode);
        var validationResult = await validation.Content.ReadFromJsonAsync<WorkflowValidationResult>();
        Assert.True(validationResult!.IsValid);
        Assert.True(validationResult.HasWarnings);
        Assert.Equal(WorkflowValidator.PublicationValidatorVersion, validationResult.ValidatorVersion);
        Assert.Equal(BuiltInWorkflowCatalog.CurrentCatalogVersion, validationResult.CatalogVersion);
        Assert.Equal("MES-AGV", validationResult.ProfileProductId);
        Assert.Equal(previewResult!.ValidatorVersion, validationResult.ValidatorVersion);
        Assert.Equal(previewResult.CatalogVersion, validationResult.CatalogVersion);
        Assert.Equal(previewResult.ProfileProductId, validationResult.ProfileProductId);
        Assert.Equal(
            previewResult.Issues.Select(issue => (issue.Code, issue.Severity, issue.NodeId, issue.EdgeId)),
            validationResult.Issues.Select(issue => (issue.Code, issue.Severity, issue.NodeId, issue.EdgeId)));

        var publish = await _client.PostAsync(
            $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/publish?actor=planner-api",
            content: null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        var version = await _client.GetFromJsonAsync<WorkflowVersion>(
            $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}");
        Assert.NotNull(version);
        Assert.Equal(WorkflowPublishStatus.Published, version!.PublishStatus);
        Assert.Equal(draft.Version, version.Definition.PublishedVersion);

        var request = new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "operator-api",
            DryRun = true
        };
        var execute = await _client.PostAsJsonAsync("/api/workflows/execute", request);
        Assert.Equal(HttpStatusCode.Accepted, execute.StatusCode);
        var accepted = await execute.Content.ReadFromJsonAsync<WorkflowExecutionResult>();
        Assert.True(accepted!.IsAccepted);
        Assert.Equal(WorkflowNodeType.Move, accepted.NextStep!.NodeType);

        var executionState = await _client.GetFromJsonAsync<WorkflowExecutionSnapshot>(
            $"/api/workflow-executions/{accepted.ExecutionId}");
        Assert.NotNull(executionState);
        Assert.Equal(WorkflowRuntimeStatus.DryRunCompleted, executionState!.RuntimeStatus);
        Assert.True(executionState.IsTerminal);

        var requestState = await _client.GetFromJsonAsync<WorkflowExecutionSnapshot>(
            $"/api/workflow-executions/by-request/{request.RequestId}");
        Assert.Equal(executionState, requestState);

        var replay = await _client.PostAsJsonAsync("/api/workflows/execute", request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayResult = await replay.Content.ReadFromJsonAsync<WorkflowExecutionResult>();
        Assert.True(replayResult!.IsIdempotentReplay);
        Assert.Equal(accepted.ExecutionId, replayResult.ExecutionId);

        var audits = await _client.GetFromJsonAsync<IReadOnlyList<WorkflowAuditResponse>>(
            $"/api/workflows/{draft.WorkflowId}/audits?version={draft.Version}");
        Assert.NotNull(audits);
        Assert.NotEmpty(audits!);
        Assert.Contains(audits, audit => audit.EventType == "WorkflowDraftCreated");
        Assert.Contains(audits, audit => audit.EventType == "WorkflowVersionPublished");
        Assert.Contains(audits, audit => audit.EventType == "WorkflowExecutionAccepted");
        Assert.All(audits, audit => Assert.Equal(draft.WorkflowId, audit.WorkflowId));
    }

    [Fact]
    public async Task Workflow_execution_read_endpoint_returns_not_found_for_unknown_ids()
    {
        var execution = await _client.GetAsync($"/api/workflow-executions/{Guid.NewGuid()}");
        var request = await _client.GetAsync($"/api/workflow-executions/by-request/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, execution.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, request.StatusCode);
    }

    [Fact]
    public async Task Workflow_audit_endpoint_returns_read_only_lifecycle_events()
    {
        var definition = WorkflowTestDefinitions.CreateMoveWorkflow();
        var create = await _client.PostAsJsonAsync("/api/workflows?actor=audit-test", definition);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var draft = await create.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(draft);

        var validate = await _client.PostAsync(
            $"/api/workflows/{draft!.WorkflowId}/versions/{draft.Version}/validate",
            content: null);
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);

        var publish = await _client.PostAsync(
            $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/publish?actor=audit-test",
            content: null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        var audits = await _client.GetAsync($"/api/workflows/{draft.WorkflowId}/audits");
        Assert.Equal(HttpStatusCode.OK, audits.StatusCode);
        using var document = JsonDocument.Parse(await audits.Content.ReadAsStringAsync());
        var events = document.RootElement.EnumerateArray()
            .Select(item => new
            {
                EventType = item.GetProperty("eventType").GetString(),
                Outcome = item.GetProperty("outcome").GetString(),
                Version = item.GetProperty("version").GetInt32(),
                Actor = item.GetProperty("actor").GetString()
            })
            .ToList();
        Assert.Equal(3, events.Count);
        Assert.Equal(
            new[] { "WorkflowDraftCreated", "WorkflowVersionValidated", "WorkflowVersionPublished" },
            events.Select(item => item.EventType));
        Assert.All(events, item => Assert.Equal(1, item.Version));
        Assert.Equal("audit-test", events[0].Actor);
        Assert.Equal("audit-test", events[2].Actor);
        Assert.Equal("Published", events[2].Outcome);
    }

    [Fact]
    public async Task Json_payload_cannot_self_authorize_a_restricted_instrument_capability()
    {
        const string injectedKey = "capabilityId";
        var valid = WorkflowTestDefinitions.CreateMoveWorkflow();
        var move = valid.Nodes.Single(node => node.NodeTypeId == WorkflowGraphNodeTypeIds.Move);
        var injected = valid with
        {
            Nodes = valid.Nodes.Select(node => node.Id == move.Id
                ? node with
                {
                    Configuration = new Dictionary<string, string?>(node.Configuration)
                    {
                        [injectedKey] = WorkflowCapabilityIds.InstrumentStartAnalysis
                    }
                }
                : node).ToArray()
        };

        var preview = await _client.PostAsJsonAsync("/api/workflows/validate", injected);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewResult = await preview.Content.ReadFromJsonAsync<WorkflowValidationResult>();
        Assert.False(previewResult!.IsValid);
        Assert.Contains(previewResult.Issues, issue =>
            issue.Code == WorkflowPublicationIssueCodes.FieldUnknown &&
            issue.NodeId == move.Id &&
            issue.ConfigurationKey == injectedKey);

        var create = await _client.PostAsJsonAsync("/api/workflows?actor=json-bypass-test", injected);
        var draft = await create.Content.ReadFromJsonAsync<WorkflowVersion>();
        var validate = await _client.PostAsync(
            $"/api/workflows/{draft!.WorkflowId}/versions/{draft.Version}/validate",
            content: null);
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);

        var publish = await _client.PostAsync(
            $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/publish?actor=json-bypass-test",
            content: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, publish.StatusCode);
    }
}
