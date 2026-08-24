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
        var unknownId = Guid.NewGuid();
        var execution = await _client.GetAsync($"/api/workflow-executions/{unknownId}");
        var request = await _client.GetAsync($"/api/workflow-executions/by-request/{Guid.NewGuid()}");
        var run = await _client.GetAsync($"/api/workflow-runs/{unknownId}");
        var nodes = await _client.GetAsync($"/api/workflow-runs/{unknownId}/nodes");
        var operations = await _client.GetAsync($"/api/workflow-runs/{unknownId}/device-operations");
        var timeline = await _client.GetAsync($"/api/workflow-runs/{unknownId}/timeline");

        Assert.Equal(HttpStatusCode.NotFound, execution.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, request.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, run.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nodes.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, operations.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, timeline.StatusCode);
    }

    [Fact]
    public async Task Workflow_run_read_endpoints_expose_prepared_node_and_timeline_without_device_activity()
    {
        var definition = WorkflowTestDefinitions.CreateMoveWorkflow();
        var create = await _client.PostAsJsonAsync("/api/workflows?actor=g4-api", definition);
        var draft = await create.Content.ReadFromJsonAsync<WorkflowVersion>();
        await _client.PostAsync(
            $"/api/workflows/{draft!.WorkflowId}/versions/{draft.Version}/validate",
            content: null);
        await _client.PostAsync(
            $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/publish?actor=g4-api",
            content: null);
        var execute = await _client.PostAsJsonAsync("/api/workflows/execute", new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "g4-api-operator",
            CorrelationId = "g4-api-read-model"
        });
        Assert.Equal(HttpStatusCode.Accepted, execute.StatusCode);
        var accepted = await execute.Content.ReadFromJsonAsync<WorkflowExecutionResult>();

        var run = await _client.GetFromJsonAsync<WorkflowExecutionSnapshot>(
            $"/api/workflow-runs/{accepted!.ExecutionId}");
        var nodes = await _client.GetFromJsonAsync<IReadOnlyList<WorkflowNodeExecutionSnapshot>>(
            $"/api/workflow-runs/{accepted.ExecutionId}/nodes");
        var operations = await _client.GetFromJsonAsync<IReadOnlyList<WorkflowDeviceOperationSnapshot>>(
            $"/api/workflow-runs/{accepted.ExecutionId}/device-operations");
        var timeline = await _client.GetFromJsonAsync<IReadOnlyList<WorkflowRunTimelineEntry>>(
            $"/api/workflow-runs/{accepted.ExecutionId}/timeline?limit=20");

        Assert.Equal(WorkflowRuntimeStatus.Prepared, run!.RuntimeStatus);
        var prepared = Assert.Single(nodes!);
        Assert.Equal(WorkflowNodeExecutionStatus.Ready, prepared.Status);
        Assert.Equal(accepted.NextStepRequest!.NodeId, prepared.NodeId);
        Assert.Empty(operations!);
        Assert.Contains(timeline!, item => item.EventType == "WorkflowExecutionAccepted");
        Assert.Contains(timeline!, item =>
            item.EventType == "WorkflowNodePrepared" && item.NodeExecutionId == prepared.Id);
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
    public async Task Typed_graph_round_trips_through_draft_validation_publish_and_version_read()
    {
        var source = WorkflowTestDefinitions.CreateMoveWorkflow();
        var definition = source with
        {
            Description = "G3 lifecycle round trip",
            Layouts = source.Nodes.Select((node, index) => new WorkflowNodeLayout
            {
                NodeId = node.Id,
                X = 120 + index * 240,
                Y = 80 + index * 25,
                Width = 210,
                Height = 125
            }).ToArray(),
            Viewport = new WorkflowCanvasViewport { X = 45, Y = 30, Zoom = 0.85 }
        };

        var create = await _client.PostAsJsonAsync("/api/workflows?actor=g3-lifecycle", definition);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var draft = await create.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(draft);

        var validate = await _client.PostAsync(
            $"/api/workflows/{draft!.WorkflowId}/versions/{draft.Version}/validate",
            content: null);
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        var validation = await validate.Content.ReadFromJsonAsync<WorkflowValidationResult>();
        Assert.True(validation!.IsValid);

        var publish = await _client.PostAsync(
            $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/publish?actor=g3-lifecycle",
            content: null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        var persisted = await _client.GetFromJsonAsync<WorkflowVersion>(
            $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}");
        Assert.NotNull(persisted);
        Assert.Equal(WorkflowVersionStatus.Published, persisted!.Status);
        Assert.Equal(WorkflowPublishStatus.Published, persisted.PublishStatus);
        Assert.Equal(draft.Version, persisted.Definition.PublishedVersion);
        Assert.Equal(definition.SchemaVersion, persisted.Definition.SchemaVersion);
        Assert.Equal(definition.Description, persisted.Definition.Description);
        Assert.Equal(
            definition.Edges.Select(edge =>
                (edge.Id, edge.SourceNodeId, edge.SourcePort, edge.TargetNodeId, edge.TargetPort, edge.Kind, edge.Priority)),
            persisted.Definition.Edges.Select(edge =>
                (edge.Id, edge.SourceNodeId, edge.SourcePort, edge.TargetNodeId, edge.TargetPort, edge.Kind, edge.Priority)));
        Assert.Equal(
            definition.Layouts.Select(layout =>
                (layout.NodeId, layout.X, layout.Y, layout.Width, layout.Height)),
            persisted.Definition.Layouts.Select(layout =>
                (layout.NodeId, layout.X, layout.Y, layout.Width, layout.Height)));
        Assert.Equal(definition.Viewport, persisted.Definition.Viewport);

        foreach (var expected in definition.Nodes)
        {
            var actual = Assert.Single(persisted.Definition.Nodes, node => node.Id == expected.Id);
            Assert.Equal(expected.NodeTypeId, actual.NodeTypeId);
            Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
            Assert.Equal(
                expected.Ports.Select(port =>
                    (port.Key, port.Direction, port.DataType, port.Cardinality, port.EdgeKind)),
                actual.Ports.Select(port =>
                    (port.Key, port.Direction, port.DataType, port.Cardinality, port.EdgeKind)));
            Assert.Equal(expected.Configuration.Count, actual.Configuration.Count);
            foreach (var (key, value) in expected.Configuration)
            {
                Assert.True(actual.Configuration.TryGetValue(key, out var actualValue));
                Assert.Equal(value, actualValue);
            }
        }
    }

    [Fact]
    public async Task G6B_condition_contract_round_trips_publishes_and_executes_without_device_activity()
    {
        var definition = WorkflowTestDefinitions.CreateConditionWorkflow();
        var create = await _client.PostAsJsonAsync("/api/workflows?actor=g6-contract", definition);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var draft = await create.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(draft);

        var persisted = await _client.GetFromJsonAsync<WorkflowVersion>(
            $"/api/workflows/{draft!.WorkflowId}/versions/{draft.Version}");
        var conditionEdge = Assert.Single(persisted!.Definition.Edges, edge =>
            edge.Kind == WorkflowEdgeKind.ConditionTrue);
        Assert.Equal(
            definition.Edges.Single(edge => edge.Kind == WorkflowEdgeKind.ConditionTrue).ConditionExpression,
            conditionEdge.ConditionExpression);

        var validate = await _client.PostAsync(
            $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/validate",
            content: null);
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        var validation = await validate.Content.ReadFromJsonAsync<WorkflowValidationResult>();
        Assert.True(validation!.IsValid);
        Assert.DoesNotContain(validation.Issues, issue =>
            issue.Code.StartsWith("WF-CONDITION-", StringComparison.Ordinal));

        var publish = await _client.PostAsync(
            $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/publish?actor=g6-contract",
            content: null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        var execute = await _client.PostAsJsonAsync("/api/workflows/execute", new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "g6-contract",
            Parameters = new Dictionary<string, string?>
            {
                ["sample.pressure"] = "10"
            }
        });
        Assert.Equal(HttpStatusCode.Accepted, execute.StatusCode);
        var accepted = await execute.Content.ReadFromJsonAsync<WorkflowExecutionResult>();
        Assert.True(accepted!.IsAccepted);

        var run = await _client.GetFromJsonAsync<WorkflowExecutionSnapshot>(
            $"/api/workflow-runs/{accepted.ExecutionId}");
        var nodes = await _client.GetFromJsonAsync<IReadOnlyList<WorkflowNodeExecutionSnapshot>>(
            $"/api/workflow-runs/{accepted.ExecutionId}/nodes");
        var operations = await _client.GetFromJsonAsync<IReadOnlyList<WorkflowDeviceOperationSnapshot>>(
            $"/api/workflow-runs/{accepted.ExecutionId}/device-operations");
        Assert.Equal(WorkflowRuntimeStatus.Completed, run!.RuntimeStatus);
        var condition = Assert.Single(nodes!);
        Assert.Equal(WorkflowGraphNodeTypeIds.Condition, condition.NodeTypeId);
        Assert.Equal(WorkflowNodeExecutionStatus.Succeeded, condition.Status);
        Assert.Empty(operations!);
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

    [Fact]
    public async Task Workflow_run_control_endpoints_enforce_configured_permissions_reason_idempotency_and_audit()
    {
        var permissions = await _client.GetFromJsonAsync<WorkflowRunControlPermissionsSnapshot>(
            "/api/workflow-run-controls/permissions?actor=local-operator");
        Assert.NotNull(permissions);
        Assert.Contains(WorkflowRunControlPermissions.Pause, permissions!.Permissions);
        Assert.Contains(WorkflowRunControlPermissions.Cancel, permissions.Permissions);
        Assert.Contains(WorkflowRunControlPermissions.ResolveUnknown, permissions.Permissions);

        var definition = WorkflowTestDefinitions.CreateMoveWorkflow();
        var create = await _client.PostAsJsonAsync("/api/workflows?actor=run-control-api", definition);
        var draft = await create.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(draft);
        Assert.Equal(
            HttpStatusCode.OK,
            (await _client.PostAsync(
                $"/api/workflows/{draft!.WorkflowId}/versions/{draft.Version}/validate",
                content: null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await _client.PostAsync(
                $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/publish?actor=run-control-api",
                content: null)).StatusCode);
        var executionResponse = await _client.PostAsJsonAsync("/api/workflows/execute", new WorkflowExecutionRequest
        {
            WorkflowId = draft.WorkflowId,
            Version = draft.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "run-control-api"
        });
        Assert.Equal(HttpStatusCode.Accepted, executionResponse.StatusCode);
        var execution = await executionResponse.Content.ReadFromJsonAsync<WorkflowExecutionResult>();
        Assert.NotNull(execution);

        var forbidden = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{execution!.ExecutionId}/pause",
            new WorkflowRunControlRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = "unconfigured-operator",
                Reason = "This actor must not self-authorize"
            });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var pauseRequest = new WorkflowRunControlRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "local-operator",
            Reason = "Verify sample identity"
        };
        var pause = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{execution.ExecutionId}/pause",
            pauseRequest);
        var pauseReplay = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{execution.ExecutionId}/pause",
            pauseRequest);
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        Assert.Equal(HttpStatusCode.OK, pauseReplay.StatusCode);
        Assert.Equal(
            WorkflowRuntimeStatus.Paused,
            (await pause.Content.ReadFromJsonAsync<WorkflowRunControlResult>())!.Run.RuntimeStatus);
        Assert.True((await pauseReplay.Content.ReadFromJsonAsync<WorkflowRunControlResult>())!.IsIdempotentReplay);

        var resume = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{execution.ExecutionId}/resume",
            new WorkflowRunControlRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = "local-operator",
                Reason = "Sample identity verified"
            });
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        Assert.Equal(
            WorkflowRuntimeStatus.Prepared,
            (await resume.Content.ReadFromJsonAsync<WorkflowRunControlResult>())!.Run.RuntimeStatus);

        var cancel = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{execution.ExecutionId}/cancel",
            new WorkflowRunControlRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = "local-operator",
                Reason = "Experiment withdrawn"
            });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(
            WorkflowRuntimeStatus.Cancelled,
            (await cancel.Content.ReadFromJsonAsync<WorkflowRunControlResult>())!.Run.RuntimeStatus);

        var invalidRepeat = await _client.PostAsJsonAsync(
            $"/api/workflow-runs/{execution.ExecutionId}/cancel",
            new WorkflowRunControlRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = "local-operator",
                Reason = "A terminal run cannot be cancelled again"
            });
        Assert.Equal(HttpStatusCode.Conflict, invalidRepeat.StatusCode);

        var timeline = await _client.GetFromJsonAsync<IReadOnlyList<WorkflowRunTimelineEntry>>(
            $"/api/workflow-runs/{execution.ExecutionId}/timeline");
        Assert.Contains(timeline!, item =>
            item.EventType == "WorkflowRunPaused" &&
            item.Actor == "local-operator" &&
            item.Reason == "Verify sample identity" &&
            item.Details.GetValueOrDefault("requestId") == pauseRequest.RequestId.ToString());
        Assert.Contains(timeline!, item => item.EventType == "WorkflowRunResumed");
        Assert.Contains(timeline!, item => item.EventType == "WorkflowRunCancelled");
    }
}
