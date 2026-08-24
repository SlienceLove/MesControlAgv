using System.Net;
using System.Net.Http.Json;
using MesControlAgv.Application;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Domain.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Tests;

public sealed class WorkflowAdvancedRuntimeTests
{
    [Fact]
    public async Task Run_input_condition_selects_typed_branch_or_default_without_device_operations()
    {
        await using var fixture = await WorkflowAdvancedRuntimeFixture.CreateAsync();
        var definition = G6WorkflowTestDefinitions.CreateRunInputConditionWorkflow(
            WorkflowConditionMissingValueBehavior.Fail);
        var version = await fixture.PublishAsync(definition);
        var matched = await fixture.ExecuteAsync(version, new Dictionary<string, string?>
        {
            ["sample.pressure"] = "10"
        });
        var unmatched = await fixture.ExecuteAsync(version, new Dictionary<string, string?>
        {
            ["sample.pressure"] = "15"
        });

        await AssertConditionEdgeAsync(
            fixture,
            matched.ExecutionId,
            definition.Edges.Single(edge => edge.Kind == WorkflowEdgeKind.ConditionTrue).Id);
        await AssertConditionEdgeAsync(
            fixture,
            unmatched.ExecutionId,
            definition.Edges.Single(edge => edge.Kind == WorkflowEdgeKind.ConditionFalse).Id);
        Assert.Empty(await fixture.Database.WorkflowDeviceOperations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Missing_condition_value_fails_or_waits_and_waiting_run_honors_controls()
    {
        await using var fixture = await WorkflowAdvancedRuntimeFixture.CreateAsync();
        var failingVersion = await fixture.PublishAsync(
            G6WorkflowTestDefinitions.CreateRunInputConditionWorkflow(
                WorkflowConditionMissingValueBehavior.Fail));

        var failed = await fixture.ExecuteAsync(failingVersion, new Dictionary<string, string?>
        {
            ["sample.pressure"] = null
        });

        var failedRun = await fixture.Service.GetExecutionAsync(failed.ExecutionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Failed, failedRun!.RuntimeStatus);
        var failedNode = Assert.Single(await fixture.Service.ListNodeExecutionsAsync(
            failed.ExecutionId,
            CancellationToken.None));
        Assert.Equal(WorkflowNodeExecutionStatus.Failed, failedNode.Status);
        Assert.Contains(await fixture.Database.WorkflowAudits.AsNoTracking().ToListAsync(), audit =>
            audit.ExecutionId == failed.ExecutionId &&
            audit.Code == WorkflowAdvancedRuntimeCodes.ConditionValueMissing);

        var waitingVersion = await fixture.PublishAsync(
            G6WorkflowTestDefinitions.CreateRunInputConditionWorkflow(
                WorkflowConditionMissingValueBehavior.Wait));
        var waiting = await fixture.ExecuteAsync(waitingVersion);
        var waitingNode = Assert.Single(await fixture.Service.ListNodeExecutionsAsync(
            waiting.ExecutionId,
            CancellationToken.None));
        Assert.Equal(WorkflowNodeExecutionStatus.WaitingForSignal, waitingNode.Status);
        Assert.Equal(
            WorkflowRuntimeStatus.Running,
            (await fixture.Service.GetExecutionAsync(waiting.ExecutionId, CancellationToken.None))!.RuntimeStatus);

        var paused = await fixture.Service.PauseRunAsync(
            waiting.ExecutionId,
            ControlRequest("Pause the unresolved condition"),
            CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Paused, paused.Run.RuntimeStatus);
        var resumed = await fixture.Service.ResumeRunAsync(
            waiting.ExecutionId,
            ControlRequest("Resume condition evaluation"),
            CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Running, resumed.Run.RuntimeStatus);
        var cancelled = await fixture.Service.CancelRunAsync(
            waiting.ExecutionId,
            ControlRequest("Cancel the unresolved experiment"),
            CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Cancelled, cancelled.Run.RuntimeStatus);
        Assert.Equal(
            WorkflowNodeExecutionStatus.Cancelled,
            Assert.Single(await fixture.Service.ListNodeExecutionsAsync(
                waiting.ExecutionId,
                CancellationToken.None)).Status);
        Assert.Empty(await fixture.Service.ListDeviceOperationsAsync(
            waiting.ExecutionId,
            CancellationToken.None));
    }

    [Fact]
    public async Task Persisted_node_output_and_unknown_resolution_drive_advanced_successors()
    {
        await using var fixture = await WorkflowAdvancedRuntimeFixture.CreateAsync();
        var outputDefinition = G6WorkflowTestDefinitions.CreateMoveConditionWorkflow(
            WorkflowConditionValueSource.NodeOutput);
        var outputVersion = await fixture.PublishAsync(outputDefinition);
        var outputRun = await fixture.ExecuteAsync(outputVersion);

        await fixture.CompleteMoveAsync(
            outputRun.ExecutionId,
            WorkflowStepCompletionOutcome.Succeeded,
            new Dictionary<string, string?> { ["stationId"] = "SAMPLE_01" });

        Assert.Equal(
            WorkflowRuntimeStatus.Completed,
            (await fixture.Service.GetExecutionAsync(outputRun.ExecutionId, CancellationToken.None))!.RuntimeStatus);
        var outputNodes = await fixture.Service.ListNodeExecutionsAsync(
            outputRun.ExecutionId,
            CancellationToken.None);
        Assert.Equal(2, outputNodes.Count);
        Assert.Equal(
            WorkflowNodeExecutionStatus.Succeeded,
            outputNodes.Single(node => node.NodeTypeId == WorkflowGraphNodeTypeIds.Condition).Status);
        Assert.Single(await fixture.Service.ListDeviceOperationsAsync(
            outputRun.ExecutionId,
            CancellationToken.None));

        var recoveryDefinition = G6WorkflowTestDefinitions.CreateMoveConditionWorkflow(
            WorkflowConditionValueSource.NodeOutput);
        var recoveryVersion = await fixture.PublishAsync(recoveryDefinition);
        var recoveryRun = await fixture.ExecuteAsync(recoveryVersion);
        var unknown = await fixture.CompleteMoveAsync(
            recoveryRun.ExecutionId,
            WorkflowStepCompletionOutcome.Unknown,
            new Dictionary<string, string?> { ["stationId"] = "SAMPLE_01" },
            error: "Unconfirmed adapter result");
        var unknownNode = Assert.Single(
            await fixture.Service.ListNodeExecutionsAsync(recoveryRun.ExecutionId, CancellationToken.None),
            node => node.Status == WorkflowNodeExecutionStatus.Unknown);

        var resolved = await fixture.Service.ResolveUnknownAsync(
            recoveryRun.ExecutionId,
            new WorkflowUnknownResolutionRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = "operator-1",
                Reason = "Persisted field evidence confirms the move succeeded",
                NodeExecutionId = unknownNode.Id,
                Outcome = WorkflowUnknownResolutionOutcome.ConfirmedSucceeded
            },
            CancellationToken.None);

        Assert.Equal(WorkflowRuntimeStatus.Unknown, unknown.RuntimeStatus);
        Assert.Equal(WorkflowRuntimeStatus.Completed, resolved.Run.RuntimeStatus);
        var recoveredNodes = await fixture.Service.ListNodeExecutionsAsync(
            recoveryRun.ExecutionId,
            CancellationToken.None);
        Assert.Equal(
            WorkflowNodeExecutionStatus.Succeeded,
            recoveredNodes.Single(node => node.NodeTypeId == WorkflowGraphNodeTypeIds.Condition).Status);
        var recoveredOperation = Assert.Single(await fixture.Service.ListDeviceOperationsAsync(
            recoveryRun.ExecutionId,
            CancellationToken.None));
        Assert.Equal("SAMPLE_01", recoveredOperation.ResultSummary["stationId"]);
        Assert.Equal("ConfirmedSucceeded", recoveredOperation.ResultSummary["resolution"]);
    }

    [Fact]
    public async Task Manual_confirmation_requires_comment_routes_outcomes_and_replays_idempotently()
    {
        await using var fixture = await WorkflowAdvancedRuntimeFixture.CreateAsync();
        var definition = G6WorkflowTestDefinitions.CreateInteractionWorkflow(
            WorkflowGraphNodeTypeIds.ManualConfirmation,
            timeoutSeconds: 60,
            requireComment: true);
        var version = await fixture.PublishAsync(definition);
        var confirmedRun = await fixture.ExecuteAsync(version);
        var confirmedNode = Assert.Single(await fixture.Service.ListNodeExecutionsAsync(
            confirmedRun.ExecutionId,
            CancellationToken.None));
        var requestId = Guid.NewGuid();
        var withoutComment = new WorkflowManualConfirmationRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "operator-1",
            Reason = "Verify required comment behavior",
            Outcome = WorkflowManualConfirmationOutcome.Confirmed
        };

        var commentError = await Assert.ThrowsAsync<WorkflowAdvancedRuntimeConflictException>(() =>
            fixture.Service.CompleteManualConfirmationAsync(
                confirmedRun.ExecutionId,
                confirmedNode.Id,
                withoutComment,
                CancellationToken.None));
        Assert.Equal(WorkflowAdvancedRuntimeCodes.ManualCommentRequired, commentError.Code);

        var request = withoutComment with
        {
            RequestId = requestId,
            Reason = "Sample identity and seal were checked",
            Comment = "Seal intact"
        };
        var applied = await fixture.Service.CompleteManualConfirmationAsync(
            confirmedRun.ExecutionId,
            confirmedNode.Id,
            request,
            CancellationToken.None);
        var replay = await fixture.Service.CompleteManualConfirmationAsync(
            confirmedRun.ExecutionId,
            confirmedNode.Id,
            request,
            CancellationToken.None);
        var changedPayload = await Assert.ThrowsAsync<WorkflowAdvancedRuntimeConflictException>(() =>
            fixture.Service.CompleteManualConfirmationAsync(
                confirmedRun.ExecutionId,
                confirmedNode.Id,
                request with { Comment = "Different replay payload" },
                CancellationToken.None));

        Assert.Equal(WorkflowRuntimeInteractionStatus.Applied, applied.Status);
        Assert.Equal(WorkflowRuntimeStatus.Completed, applied.Run.RuntimeStatus);
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(WorkflowAdvancedRuntimeCodes.RequestIdReused, changedPayload.Code);
        var confirmed = Assert.Single(await fixture.Service.ListNodeExecutionsAsync(
            confirmedRun.ExecutionId,
            CancellationToken.None));
        Assert.Equal(WorkflowNodeExecutionStatus.Succeeded, confirmed.Status);
        Assert.Equal("operator-1", confirmed.Outputs["confirmedBy"]);
        Assert.Equal("Seal intact", confirmed.Outputs["comment"]);

        var cancelledRun = await fixture.ExecuteAsync(version);
        var cancelledNode = Assert.Single(await fixture.Service.ListNodeExecutionsAsync(
            cancelledRun.ExecutionId,
            CancellationToken.None));
        var cancelled = await fixture.Service.CompleteManualConfirmationAsync(
            cancelledRun.ExecutionId,
            cancelledNode.Id,
            new WorkflowManualConfirmationRequest
            {
                RequestId = Guid.NewGuid(),
                Actor = "operator-1",
                Reason = "Sample was withdrawn",
                Outcome = WorkflowManualConfirmationOutcome.Cancelled,
                Comment = "Withdrawn by laboratory"
            },
            CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Completed, cancelled.Run.RuntimeStatus);
        Assert.Equal(
            WorkflowNodeExecutionStatus.Cancelled,
            Assert.Single(await fixture.Service.ListNodeExecutionsAsync(
                cancelledRun.ExecutionId,
                CancellationToken.None)).Status);
        Assert.Empty(await fixture.Database.WorkflowDeviceOperations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Interaction_timeout_is_deterministic_while_paused_and_for_a_late_signal()
    {
        var clock = new AdjustableTimeProvider(
            new DateTimeOffset(2026, 8, 24, 8, 0, 0, TimeSpan.Zero));
        await using var fixture = await WorkflowAdvancedRuntimeFixture.CreateAsync(clock);
        var manualVersion = await fixture.PublishAsync(
            G6WorkflowTestDefinitions.CreateInteractionWorkflow(
                WorkflowGraphNodeTypeIds.ManualConfirmation,
                timeoutSeconds: 10));
        var manualRun = await fixture.ExecuteAsync(manualVersion);
        await fixture.Service.PauseRunAsync(
            manualRun.ExecutionId,
            ControlRequest("Hold future scheduling"),
            CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(11));

        var timeoutTrigger = await fixture.Service.SubmitExternalSignalAsync(
            manualRun.ExecutionId,
            SignalRequest("unrelated.signal", "manual-timeout"),
            CancellationToken.None);

        Assert.Equal(WorkflowRuntimeStatus.Completed, timeoutTrigger.Run.RuntimeStatus);
        Assert.Equal(
            WorkflowNodeExecutionStatus.TimedOut,
            Assert.Single(await fixture.Service.ListNodeExecutionsAsync(
                manualRun.ExecutionId,
                CancellationToken.None)).Status);
        Assert.Equal(WorkflowRuntimeInteractionStatus.Pending, timeoutTrigger.Status);

        var signalVersion = await fixture.PublishAsync(
            G6WorkflowTestDefinitions.CreateInteractionWorkflow(
                WorkflowGraphNodeTypeIds.SignalWait,
                timeoutSeconds: 5));
        var signalRun = await fixture.ExecuteAsync(signalVersion, new Dictionary<string, string?>
        {
            ["sample.batch-id"] = "B-LATE"
        });
        clock.Advance(TimeSpan.FromSeconds(6));
        var lateRequest = SignalRequest("lims.result-ready", "B-LATE");

        var late = await fixture.Service.SubmitExternalSignalAsync(
            signalRun.ExecutionId,
            lateRequest,
            CancellationToken.None);
        var replay = await fixture.Service.SubmitExternalSignalAsync(
            signalRun.ExecutionId,
            lateRequest,
            CancellationToken.None);

        Assert.Equal(WorkflowRuntimeInteractionStatus.Pending, late.Status);
        Assert.Equal(WorkflowRuntimeStatus.Completed, late.Run.RuntimeStatus);
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(
            WorkflowNodeExecutionStatus.TimedOut,
            Assert.Single(await fixture.Service.ListNodeExecutionsAsync(
                signalRun.ExecutionId,
                CancellationToken.None)).Status);
        Assert.Empty(await fixture.Database.WorkflowDeviceOperations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task External_signal_matches_exactly_and_preserves_pending_and_replay_evidence()
    {
        await using var fixture = await WorkflowAdvancedRuntimeFixture.CreateAsync();
        var version = await fixture.PublishAsync(
            G6WorkflowTestDefinitions.CreateInteractionWorkflow(
                WorkflowGraphNodeTypeIds.SignalWait,
                timeoutSeconds: 300));
        var missingCorrelation = await fixture.ExecuteAsync(version);
        var missingCorrelationRun = await fixture.Service.GetExecutionAsync(
            missingCorrelation.ExecutionId,
            CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Failed, missingCorrelationRun!.RuntimeStatus);
        Assert.Contains(await fixture.Database.WorkflowAudits.AsNoTracking().ToListAsync(), audit =>
            audit.ExecutionId == missingCorrelation.ExecutionId &&
            audit.Code == WorkflowAdvancedRuntimeCodes.SignalCorrelationMissing);

        var run = await fixture.ExecuteAsync(version, new Dictionary<string, string?>
        {
            ["sample.batch-id"] = "B-100"
        });
        var wrong = await fixture.Service.SubmitExternalSignalAsync(
            run.ExecutionId,
            SignalRequest("lims.result-ready", "B-OTHER"),
            CancellationToken.None);
        Assert.Equal(WorkflowRuntimeInteractionStatus.Pending, wrong.Status);
        Assert.Equal(WorkflowRuntimeStatus.Running, wrong.Run.RuntimeStatus);

        var request = SignalRequest("lims.result-ready", "B-100") with
        {
            Data = new Dictionary<string, string?> { ["result"] = "ready" }
        };
        var applied = await fixture.Service.SubmitExternalSignalAsync(
            run.ExecutionId,
            request,
            CancellationToken.None);
        var replay = await fixture.Service.SubmitExternalSignalAsync(
            run.ExecutionId,
            request,
            CancellationToken.None);
        var conflict = await Assert.ThrowsAsync<WorkflowAdvancedRuntimeConflictException>(() =>
            fixture.Service.SubmitExternalSignalAsync(
                run.ExecutionId,
                request with
                {
                    Data = new Dictionary<string, string?> { ["result"] = "changed" }
                },
                CancellationToken.None));

        Assert.Equal(WorkflowRuntimeInteractionStatus.Applied, applied.Status);
        Assert.Equal(WorkflowRuntimeStatus.Completed, applied.Run.RuntimeStatus);
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(WorkflowAdvancedRuntimeCodes.RequestIdReused, conflict.Code);
        var interactions = await fixture.Service.ListRuntimeInteractionsAsync(
            run.ExecutionId,
            CancellationToken.None);
        Assert.Equal(2, interactions.Count);
        Assert.Contains(interactions, item => item.Status == WorkflowRuntimeInteractionStatus.Pending);
        Assert.Contains(interactions, item =>
            item.Status == WorkflowRuntimeInteractionStatus.Applied &&
            item.Data.GetValueOrDefault("result") == "ready");
        var node = Assert.Single(await fixture.Service.ListNodeExecutionsAsync(
            run.ExecutionId,
            CancellationToken.None));
        Assert.Equal(WorkflowNodeExecutionStatus.Succeeded, node.Status);
        Assert.Equal(request.RequestId.ToString(), node.Outputs["signalId"]);
        Assert.Empty(await fixture.Service.ListDeviceOperationsAsync(run.ExecutionId, CancellationToken.None));
    }

    [Fact]
    public async Task Early_signal_is_consumed_after_service_restart_when_the_wait_becomes_current()
    {
        await using var fixture = await WorkflowAdvancedRuntimeFixture.CreateAsync();
        var definition = G6WorkflowTestDefinitions.CreateMoveSignalWorkflow();
        var version = await fixture.PublishAsync(definition);
        var run = await fixture.ExecuteAsync(version, new Dictionary<string, string?>
        {
            ["sample.batch-id"] = "B-RESTART"
        });
        var request = SignalRequest("lims.result-ready", "B-RESTART");
        var early = await fixture.Service.SubmitExternalSignalAsync(
            run.ExecutionId,
            request,
            CancellationToken.None);
        Assert.Equal(WorkflowRuntimeInteractionStatus.Pending, early.Status);

        await fixture.RestartAsync();
        await fixture.CompleteMoveAsync(
            run.ExecutionId,
            WorkflowStepCompletionOutcome.Succeeded,
            new Dictionary<string, string?> { ["stationId"] = "SAMPLE_01" });

        Assert.Equal(
            WorkflowRuntimeStatus.Completed,
            (await fixture.Service.GetExecutionAsync(run.ExecutionId, CancellationToken.None))!.RuntimeStatus);
        var interactions = await fixture.Service.ListRuntimeInteractionsAsync(
            run.ExecutionId,
            CancellationToken.None);
        var consumed = Assert.Single(interactions);
        Assert.Equal(WorkflowRuntimeInteractionStatus.Applied, consumed.Status);
        Assert.NotNull(consumed.NodeExecutionId);
        var nodes = await fixture.Service.ListNodeExecutionsAsync(run.ExecutionId, CancellationToken.None);
        Assert.Equal(2, nodes.Count);
        Assert.Equal(
            consumed.NodeExecutionId,
            nodes.Single(node => node.NodeTypeId == WorkflowGraphNodeTypeIds.SignalWait).Id);
        Assert.Single(await fixture.Service.ListDeviceOperationsAsync(run.ExecutionId, CancellationToken.None));
    }

    private static async Task AssertConditionEdgeAsync(
        WorkflowAdvancedRuntimeFixture fixture,
        Guid executionId,
        Guid expectedEdgeId)
    {
        var run = await fixture.Service.GetExecutionAsync(executionId, CancellationToken.None);
        Assert.Equal(WorkflowRuntimeStatus.Completed, run!.RuntimeStatus);
        var condition = Assert.Single(await fixture.Service.ListNodeExecutionsAsync(
            executionId,
            CancellationToken.None));
        Assert.Equal(WorkflowNodeExecutionStatus.Succeeded, condition.Status);
        Assert.Equal(expectedEdgeId.ToString(), condition.Outputs["matchedEdgeId"]);
        Assert.NotNull(condition.Inputs["branch.0.sourceValue"]);
    }

    private static WorkflowRunControlRequest ControlRequest(string reason) => new()
    {
        RequestId = Guid.NewGuid(),
        Actor = "operator-1",
        Reason = reason
    };

    private static WorkflowExternalSignalRequest SignalRequest(string name, string correlation) => new()
    {
        RequestId = Guid.NewGuid(),
        Actor = "operator-1",
        Reason = "Deliver external laboratory evidence",
        SignalName = name,
        CorrelationValue = correlation
    };
}

public sealed class WorkflowAdvancedRuntimeApiTests
{
    [Fact]
    public async Task Signal_endpoints_enforce_permissions_status_codes_replay_and_read_model()
    {
        await using var factory = new MesWebApplicationFactory();
        using var client = factory.CreateClient();
        var permissions = await client.GetFromJsonAsync<WorkflowRunControlPermissionsSnapshot>(
            "/api/workflow-run-controls/permissions?actor=local-operator");
        Assert.Contains(WorkflowRunControlPermissions.SubmitSignal, permissions!.Permissions);
        Assert.Contains(WorkflowRunControlPermissions.CompleteManualTask, permissions.Permissions);

        var version = await PublishAsync(
            client,
            G6WorkflowTestDefinitions.CreateInteractionWorkflow(
                WorkflowGraphNodeTypeIds.SignalWait,
                timeoutSeconds: 300));
        var execute = await client.PostAsJsonAsync("/api/workflows/execute", new WorkflowExecutionRequest
        {
            WorkflowId = version.WorkflowId,
            Version = version.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "g6-api",
            Parameters = new Dictionary<string, string?> { ["sample.batch-id"] = "B-HTTP" }
        });
        Assert.Equal(HttpStatusCode.Accepted, execute.StatusCode);
        var run = await execute.Content.ReadFromJsonAsync<WorkflowExecutionResult>();

        var forbiddenRequest = new WorkflowExternalSignalRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "unconfigured-operator",
            Reason = "Must not self-authorize",
            SignalName = "lims.result-ready",
            CorrelationValue = "B-HTTP"
        };
        var forbidden = await client.PostAsJsonAsync(
            $"/api/workflow-runs/{run!.ExecutionId}/signals",
            forbiddenRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var pendingRequest = forbiddenRequest with
        {
            RequestId = Guid.NewGuid(),
            Actor = "local-operator",
            Reason = "Retain unmatched correlation evidence",
            CorrelationValue = "B-OTHER"
        };
        var pending = await client.PostAsJsonAsync(
            $"/api/workflow-runs/{run.ExecutionId}/signals",
            pendingRequest);
        var pendingReplay = await client.PostAsJsonAsync(
            $"/api/workflow-runs/{run.ExecutionId}/signals",
            pendingRequest);
        Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
        Assert.Equal(HttpStatusCode.OK, pendingReplay.StatusCode);
        Assert.True((await pendingReplay.Content.ReadFromJsonAsync<WorkflowRuntimeInteractionResult>())!
            .IsIdempotentReplay);

        var appliedRequest = pendingRequest with
        {
            RequestId = Guid.NewGuid(),
            Reason = "Apply matching laboratory evidence",
            CorrelationValue = "B-HTTP"
        };
        var applied = await client.PostAsJsonAsync(
            $"/api/workflow-runs/{run.ExecutionId}/signals",
            appliedRequest);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(
            WorkflowRuntimeInteractionStatus.Applied,
            (await applied.Content.ReadFromJsonAsync<WorkflowRuntimeInteractionResult>())!.Status);

        var conflict = await client.PostAsJsonAsync(
            $"/api/workflow-runs/{run.ExecutionId}/signals",
            appliedRequest with { Reason = "Changed replay payload" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var interactions = await client.GetFromJsonAsync<IReadOnlyList<WorkflowRuntimeInteractionSnapshot>>(
            $"/api/workflow-runs/{run.ExecutionId}/interactions");
        Assert.Equal(2, interactions!.Count);
        Assert.Contains(interactions, item => item.Status == WorkflowRuntimeInteractionStatus.Pending);
        Assert.Contains(interactions, item => item.Status == WorkflowRuntimeInteractionStatus.Applied);
    }

    [Fact]
    public async Task Manual_confirmation_endpoint_maps_authorization_validation_and_durable_replay()
    {
        await using var factory = new MesWebApplicationFactory();
        using var client = factory.CreateClient();
        var version = await PublishAsync(
            client,
            G6WorkflowTestDefinitions.CreateInteractionWorkflow(
                WorkflowGraphNodeTypeIds.ManualConfirmation,
                timeoutSeconds: 300,
                requireComment: true));
        var execute = await client.PostAsJsonAsync("/api/workflows/execute", new WorkflowExecutionRequest
        {
            WorkflowId = version.WorkflowId,
            Version = version.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "g6-manual-api"
        });
        execute.EnsureSuccessStatusCode();
        var run = await execute.Content.ReadFromJsonAsync<WorkflowExecutionResult>();
        var nodes = await client.GetFromJsonAsync<IReadOnlyList<WorkflowNodeExecutionSnapshot>>(
            $"/api/workflow-runs/{run!.ExecutionId}/nodes");
        var node = Assert.Single(nodes!);
        var endpoint =
            $"/api/workflow-runs/{run.ExecutionId}/nodes/{node.Id}/manual-confirmation";

        var forbidden = await client.PostAsJsonAsync(endpoint, new WorkflowManualConfirmationRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "unconfigured-operator",
            Reason = "Must not self-authorize",
            Outcome = WorkflowManualConfirmationOutcome.Confirmed,
            Comment = "Unauthorized"
        });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var request = new WorkflowManualConfirmationRequest
        {
            RequestId = Guid.NewGuid(),
            Actor = "local-operator",
            Reason = "Confirm HTTP manual task",
            Outcome = WorkflowManualConfirmationOutcome.Confirmed
        };
        var missingComment = await client.PostAsJsonAsync(endpoint, request);
        Assert.Equal(HttpStatusCode.Conflict, missingComment.StatusCode);
        Assert.Contains(
            WorkflowAdvancedRuntimeCodes.ManualCommentRequired,
            await missingComment.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        request = request with { Comment = "Verified through the controlled UI" };
        var applied = await client.PostAsJsonAsync(endpoint, request);
        var replay = await client.PostAsJsonAsync(endpoint, request);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(
            WorkflowRuntimeStatus.Completed,
            (await applied.Content.ReadFromJsonAsync<WorkflowRuntimeInteractionResult>())!.Run.RuntimeStatus);
        Assert.True((await replay.Content.ReadFromJsonAsync<WorkflowRuntimeInteractionResult>())!
            .IsIdempotentReplay);
        var interactions = await client.GetFromJsonAsync<IReadOnlyList<WorkflowRuntimeInteractionSnapshot>>(
            $"/api/workflow-runs/{run.ExecutionId}/interactions");
        var interaction = Assert.Single(interactions!);
        Assert.Equal(WorkflowRuntimeInteractionType.ManualConfirmation, interaction.InteractionType);
        Assert.Equal("local-operator", interaction.Actor);
        Assert.Equal("Verified through the controlled UI", interaction.Data["comment"]);
    }

    private static async Task<WorkflowVersion> PublishAsync(HttpClient client, WorkflowDefinition definition)
    {
        var create = await client.PostAsJsonAsync("/api/workflows?actor=g6-api", definition);
        create.EnsureSuccessStatusCode();
        var draft = await create.Content.ReadFromJsonAsync<WorkflowVersion>();
        var validate = await client.PostAsync(
            $"/api/workflows/{draft!.WorkflowId}/versions/{draft.Version}/validate",
            content: null);
        validate.EnsureSuccessStatusCode();
        Assert.True((await validate.Content.ReadFromJsonAsync<WorkflowValidationResult>())!.IsValid);
        var publish = await client.PostAsync(
            $"/api/workflows/{draft.WorkflowId}/versions/{draft.Version}/publish?actor=g6-api",
            content: null);
        publish.EnsureSuccessStatusCode();
        return (await publish.Content.ReadFromJsonAsync<WorkflowVersion>())!;
    }
}

internal sealed class WorkflowAdvancedRuntimeFixture : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IWorkflowRunControlAuthorizer _authorizer;

    private WorkflowAdvancedRuntimeFixture(
        SqliteConnection connection,
        AdjustableTimeProvider clock,
        IWorkflowRunControlAuthorizer authorizer,
        MesDbContext database,
        WorkflowApplicationService service)
    {
        _connection = connection;
        Clock = clock;
        _authorizer = authorizer;
        Database = database;
        Service = service;
    }

    public AdjustableTimeProvider Clock { get; }
    public MesDbContext Database { get; private set; }
    public WorkflowApplicationService Service { get; private set; }

    public static async Task<WorkflowAdvancedRuntimeFixture> CreateAsync(
        AdjustableTimeProvider? clock = null)
    {
        clock ??= new AdjustableTimeProvider(
            new DateTimeOffset(2026, 8, 24, 6, 0, 0, TimeSpan.Zero));
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var authorizer = new ConfiguredWorkflowRunControlAuthorizer(Options.Create(
            new WorkflowRunControlAuthorizationOptions
            {
                Operators =
                [
                    new WorkflowRunControlOperatorOptions
                    {
                        Name = "operator-1",
                        Permissions =
                        [
                            WorkflowRunControlPermissions.Pause,
                            WorkflowRunControlPermissions.Cancel,
                            WorkflowRunControlPermissions.ResolveUnknown,
                            WorkflowRunControlPermissions.SubmitSignal,
                            WorkflowRunControlPermissions.CompleteManualTask
                        ]
                    }
                ]
            }));
        var database = CreateDatabase(connection);
        await database.Database.EnsureCreatedAsync();
        var service = CreateService(database, clock, authorizer);
        return new WorkflowAdvancedRuntimeFixture(connection, clock, authorizer, database, service);
    }

    public async Task<WorkflowVersion> PublishAsync(WorkflowDefinition definition)
    {
        var draft = await Service.CreateDraftAsync(definition, "g6-test", CancellationToken.None);
        var validation = await Service.ValidateVersionAsync(
            draft.WorkflowId,
            draft.Version,
            CancellationToken.None);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Issues.Select(issue =>
            $"{issue.Code}: {issue.Message}")));
        return await Service.PublishAsync(
            draft.WorkflowId,
            draft.Version,
            "g6-test",
            CancellationToken.None);
    }

    public async Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowVersion version,
        IReadOnlyDictionary<string, string?>? parameters = null)
    {
        var result = await Service.ExecuteAsync(new WorkflowExecutionRequest
        {
            WorkflowId = version.WorkflowId,
            Version = version.Version,
            RequestId = Guid.NewGuid(),
            RequestedBy = "g6-test",
            Parameters = parameters ?? new Dictionary<string, string?>()
        }, CancellationToken.None);
        Assert.True(result.IsAccepted, result.RejectionReason);
        return result;
    }

    public async Task<WorkflowExecutionSnapshot> CompleteMoveAsync(
        Guid workflowRunId,
        WorkflowStepCompletionOutcome outcome,
        IReadOnlyDictionary<string, string?>? outputs = null,
        string? error = null)
    {
        var work = Assert.Single(
            await Service.ListSimulatorDispatchableNodesAsync(CancellationToken.None),
            item => item.NodeExecution.WorkflowRunId == workflowRunId);
        var claimed = await Service.ClaimNodeExecutionAsync(work.NodeExecution.Id, CancellationToken.None);
        return await Service.CompleteNodeExecutionAsync(
            claimed.NodeExecution.Id,
            new WorkflowNodeExecutionCompletionRequest
            {
                DeviceOperationId = claimed.DeviceOperation!.OperationId,
                Outcome = outcome,
                Error = error,
                Outputs = outputs ?? new Dictionary<string, string?>()
            },
            CancellationToken.None);
    }

    public async Task RestartAsync()
    {
        await Database.DisposeAsync();
        Database = CreateDatabase(_connection);
        Service = CreateService(Database, Clock, _authorizer);
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static MesDbContext CreateDatabase(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<MesDbContext>().UseSqlite(connection).Options);

    private static WorkflowApplicationService CreateService(
        MesDbContext database,
        TimeProvider clock,
        IWorkflowRunControlAuthorizer authorizer)
    {
        var validator = new WorkflowValidator();
        var reader = new MesWorkflowVersionReader(database);
        return new WorkflowApplicationService(
            database,
            reader,
            new WorkflowRuntimeExecutor(
                reader,
                validator,
                admissionPolicies: [new ActiveProfileWorkflowAdmissionPolicy(ProfileConfiguration.Default)]),
            validator,
            clock,
            authorizer);
    }
}

internal static class G6WorkflowTestDefinitions
{
    private static readonly WorkflowCatalogSet Catalog = BuiltInWorkflowCatalog.Create();

    public static WorkflowDefinition CreateRunInputConditionWorkflow(
        WorkflowConditionMissingValueBehavior missingValueBehavior)
    {
        var start = Node(WorkflowGraphNodeTypeIds.Start, "Start", 1);
        var condition = Node(WorkflowGraphNodeTypeIds.Condition, "Pressure gate", 2);
        var end = Node(WorkflowGraphNodeTypeIds.End, "End", 3);
        return Workflow(
            "G6 run input condition",
            [start, condition, end],
            [
                Edge(start, "success", condition),
                Edge(condition, "condition", end, WorkflowEdgeKind.ConditionTrue, 10) with
                {
                    ConditionExpression = new WorkflowConditionExpression
                    {
                        Source = WorkflowConditionValueSource.RunInput,
                        SourceKey = "sample.pressure",
                        ValueType = WorkflowSchemaValueType.Decimal,
                        Operator = WorkflowConditionOperator.LessThanOrEqual,
                        CompareValue = "12",
                        MissingValueBehavior = missingValueBehavior
                    }
                },
                Edge(condition, "default", end, WorkflowEdgeKind.ConditionFalse)
            ]);
    }

    public static WorkflowDefinition CreateMoveConditionWorkflow(WorkflowConditionValueSource source)
    {
        var start = Node(WorkflowGraphNodeTypeIds.Start, "Start", 1);
        var move = Node(
            WorkflowGraphNodeTypeIds.Move,
            "Move sample",
            2,
            new Dictionary<string, string?>
            {
                [WorkflowNodeConfigurationKeys.TargetStation] = "SAMPLE_01",
                [WorkflowNodeConfigurationKeys.TimeoutSeconds] = "300",
                [WorkflowNodeConfigurationKeys.RetryCount] = "0"
            });
        var condition = Node(WorkflowGraphNodeTypeIds.Condition, "Route gate", 3);
        var end = Node(WorkflowGraphNodeTypeIds.End, "End", 4);
        var expression = source == WorkflowConditionValueSource.NodeOutput
            ? new WorkflowConditionExpression
            {
                Source = source,
                SourceNodeId = move.Id,
                SourceKey = "stationId",
                ValueType = WorkflowSchemaValueType.String,
                Operator = WorkflowConditionOperator.Equal,
                CompareValue = "SAMPLE_01",
                MissingValueBehavior = WorkflowConditionMissingValueBehavior.Fail
            }
            : new WorkflowConditionExpression
            {
                Source = source,
                SourceKey = "sample.route",
                ValueType = WorkflowSchemaValueType.String,
                Operator = WorkflowConditionOperator.Equal,
                CompareValue = "continue",
                MissingValueBehavior = WorkflowConditionMissingValueBehavior.Fail
            };
        return Workflow(
            "G6 move condition",
            [start, move, condition, end],
            [
                Edge(start, "success", move),
                Edge(move, "success", condition),
                Edge(condition, "condition", end, WorkflowEdgeKind.ConditionTrue, 10) with
                {
                    ConditionExpression = expression
                },
                Edge(condition, "default", end, WorkflowEdgeKind.ConditionFalse)
            ]);
    }

    public static WorkflowDefinition CreateInteractionWorkflow(
        string nodeTypeId,
        int timeoutSeconds,
        bool requireComment = false)
    {
        var start = Node(WorkflowGraphNodeTypeIds.Start, "Start", 1);
        var configuration = string.Equals(
            nodeTypeId,
            WorkflowGraphNodeTypeIds.ManualConfirmation,
            StringComparison.Ordinal)
            ? new Dictionary<string, string?>
            {
                [WorkflowNodeConfigurationKeys.Prompt] = "Confirm the sample is ready.",
                [WorkflowNodeConfigurationKeys.TimeoutSeconds] = timeoutSeconds.ToString(),
                [WorkflowNodeConfigurationKeys.RequireComment] = requireComment.ToString()
            }
            : new Dictionary<string, string?>
            {
                [WorkflowNodeConfigurationKeys.SignalName] = "lims.result-ready",
                [WorkflowNodeConfigurationKeys.CorrelationKey] = "sample.batch-id",
                [WorkflowNodeConfigurationKeys.TimeoutSeconds] = timeoutSeconds.ToString()
            };
        var interaction = Node(nodeTypeId, "Interaction wait", 2, configuration);
        var end = Node(WorkflowGraphNodeTypeIds.End, "End", 3);
        return Workflow(
            "G6 interaction workflow",
            [start, interaction, end],
            [
                Edge(start, "success", interaction),
                Edge(interaction, "success", end),
                Edge(interaction, "timeout", end, WorkflowEdgeKind.Timeout),
                Edge(interaction, "cancelled", end, WorkflowEdgeKind.Cancelled)
            ]);
    }

    public static WorkflowDefinition CreateMoveSignalWorkflow()
    {
        var start = Node(WorkflowGraphNodeTypeIds.Start, "Start", 1);
        var move = Node(
            WorkflowGraphNodeTypeIds.Move,
            "Move sample",
            2,
            new Dictionary<string, string?>
            {
                [WorkflowNodeConfigurationKeys.TargetStation] = "SAMPLE_01",
                [WorkflowNodeConfigurationKeys.TimeoutSeconds] = "300",
                [WorkflowNodeConfigurationKeys.RetryCount] = "0"
            });
        var signal = Node(
            WorkflowGraphNodeTypeIds.SignalWait,
            "Wait for LIMS",
            3,
            new Dictionary<string, string?>
            {
                [WorkflowNodeConfigurationKeys.SignalName] = "lims.result-ready",
                [WorkflowNodeConfigurationKeys.CorrelationKey] = "sample.batch-id",
                [WorkflowNodeConfigurationKeys.TimeoutSeconds] = "300"
            });
        var end = Node(WorkflowGraphNodeTypeIds.End, "End", 4);
        return Workflow(
            "G6 early signal workflow",
            [start, move, signal, end],
            [
                Edge(start, "success", move),
                Edge(move, "success", signal),
                Edge(signal, "success", end),
                Edge(signal, "timeout", end, WorkflowEdgeKind.Timeout),
                Edge(signal, "cancelled", end, WorkflowEdgeKind.Cancelled)
            ]);
    }

    private static WorkflowNode Node(
        string nodeTypeId,
        string name,
        int order,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var definition = Catalog.NodeTypes.GetLatest(nodeTypeId) ??
                         throw new InvalidOperationException($"Missing G6 test node '{nodeTypeId}'.");
        configuration ??= new Dictionary<string, string?>();
        configuration.TryGetValue(WorkflowNodeConfigurationKeys.TargetStation, out var targetStation);
        return new WorkflowNode
        {
            Id = Guid.NewGuid(),
            Type = WorkflowGraphNodeTypeIds.ToContractType(nodeTypeId),
            NodeTypeId = nodeTypeId,
            SchemaVersion = definition.SchemaVersion,
            Name = name,
            Order = order,
            TargetStation = targetStation,
            Ports = definition.Ports,
            Configuration = new Dictionary<string, string?>(configuration, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static WorkflowEdgeDefinition Edge(
        WorkflowNode source,
        string sourcePort,
        WorkflowNode target,
        WorkflowEdgeKind kind = WorkflowEdgeKind.Success,
        int priority = 0) => new()
    {
        SourceNodeId = source.Id,
        SourcePort = sourcePort,
        TargetNodeId = target.Id,
        TargetPort = "in",
        Kind = kind,
        Priority = priority
    };

    private static WorkflowDefinition Workflow(
        string name,
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlyList<WorkflowEdgeDefinition> edges) => new()
    {
        Id = Guid.NewGuid(),
        SchemaVersion = WorkflowGraphDocument.CurrentSchemaVersion,
        Name = name,
        Nodes = nodes.Select(node => node with
        {
            NextNodeIds = edges
                .Where(edge => edge.SourceNodeId == node.Id)
                .OrderBy(edge => edge.Priority)
                .ThenBy(edge => edge.Id)
                .Select(edge => edge.TargetNodeId)
                .Distinct()
                .ToArray()
        }).ToArray(),
        Edges = edges
    };
}

internal sealed class AdjustableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
{
    private DateTimeOffset _utcNow = initialUtc;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
}
