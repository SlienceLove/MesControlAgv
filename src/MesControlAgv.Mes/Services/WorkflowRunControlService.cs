using System.Security.Cryptography;
using System.Text;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed partial class WorkflowApplicationService
{
    private static readonly string[] ControlEventTypes =
    [
        "WorkflowRunPaused",
        "WorkflowRunResumed",
        "WorkflowRunCancelled",
        "WorkflowUnknownResolved"
    ];

    public Task<WorkflowRunControlResult> PauseRunAsync(
        Guid workflowRunId,
        WorkflowRunControlRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAdvancedRuntimeSerializedAsync(
            () => PauseRunCoreAsync(workflowRunId, request, cancellationToken),
            cancellationToken);

    private async Task<WorkflowRunControlResult> PauseRunCoreAsync(
        Guid workflowRunId,
        WorkflowRunControlRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeControlRequest(request);
        _controlAuthorizer.Demand(normalized.Actor, WorkflowRunControlPermissions.Pause);
        var fingerprint = CreateControlFingerprint(
            WorkflowRunControlAction.Pause,
            workflowRunId,
            normalized.Actor,
            normalized.Reason);
        var replay = await TryReplayControlAsync(
            workflowRunId,
            normalized.RequestId,
            WorkflowRunControlAction.Pause,
            fingerprint,
            cancellationToken);
        if (replay is not null) return replay;

        await EnsureLegacySimulatorRuntimeRecordsAsync(cancellationToken);
        var run = await FindExecutionAsync(workflowRunId, cancellationToken);
        var current = WorkflowPersistence.ToExecutionSnapshot(run).RuntimeStatus;
        if (current is not WorkflowRuntimeStatus.Prepared and not WorkflowRuntimeStatus.Running)
        {
            throw new WorkflowRunControlConflictException(
                $"A workflow run in '{current}' state cannot be paused.");
        }

        run.RuntimeStatus = WorkflowRuntimeStatus.Paused.ToString();
        run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        AddControlAudit(
            run,
            normalized,
            WorkflowRunControlAction.Pause,
            "WorkflowRunPaused",
            WorkflowRuntimeStatus.Paused.ToString(),
            WorkflowRunControlPermissions.Pause,
            fingerprint,
            new Dictionary<string, string?>
            {
                ["previousRuntimeStatus"] = current.ToString(),
                ["pauseSemantics"] = "FutureClaimsOnly"
            });
        await _database.SaveChangesAsync(cancellationToken);
        return CreateControlResult(run, normalized.RequestId, WorkflowRunControlAction.Pause);
    }

    public Task<WorkflowRunControlResult> ResumeRunAsync(
        Guid workflowRunId,
        WorkflowRunControlRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAdvancedRuntimeSerializedAsync(
            () => ResumeRunCoreAsync(workflowRunId, request, cancellationToken),
            cancellationToken);

    private async Task<WorkflowRunControlResult> ResumeRunCoreAsync(
        Guid workflowRunId,
        WorkflowRunControlRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeControlRequest(request);
        _controlAuthorizer.Demand(normalized.Actor, WorkflowRunControlPermissions.Pause);
        var fingerprint = CreateControlFingerprint(
            WorkflowRunControlAction.Resume,
            workflowRunId,
            normalized.Actor,
            normalized.Reason);
        var replay = await TryReplayControlAsync(
            workflowRunId,
            normalized.RequestId,
            WorkflowRunControlAction.Resume,
            fingerprint,
            cancellationToken);
        if (replay is not null) return replay;

        var run = await FindExecutionAsync(workflowRunId, cancellationToken);
        var current = WorkflowPersistence.ToExecutionSnapshot(run).RuntimeStatus;
        if (current != WorkflowRuntimeStatus.Paused)
        {
            throw new WorkflowRunControlConflictException(
                $"Only a paused workflow run can be resumed; current state is '{current}'.");
        }

        var activeStatuses = await _database.WorkflowNodeExecutions
            .AsNoTracking()
            .Where(item => item.WorkflowRunId == workflowRunId &&
                           (item.Status == WorkflowNodeExecutionStatus.Running.ToString() ||
                            item.Status == WorkflowNodeExecutionStatus.Ready.ToString() ||
                            item.Status == WorkflowNodeExecutionStatus.WaitingForSignal.ToString()))
            .Select(item => item.Status)
            .ToListAsync(cancellationToken);
        var resumedStatus =
            activeStatuses.Contains(WorkflowNodeExecutionStatus.Running.ToString(), StringComparer.Ordinal) ||
            activeStatuses.Contains(WorkflowNodeExecutionStatus.WaitingForSignal.ToString(), StringComparer.Ordinal)
                ? WorkflowRuntimeStatus.Running
                : activeStatuses.Contains(WorkflowNodeExecutionStatus.Ready.ToString(), StringComparer.Ordinal) ||
                  !string.IsNullOrWhiteSpace(run.PendingStepJson)
                    ? WorkflowRuntimeStatus.Prepared
                    : throw new WorkflowRunControlConflictException(
                        "The paused workflow run has no active or pending node to resume.");

        run.RuntimeStatus = resumedStatus.ToString();
        run.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        AddControlAudit(
            run,
            normalized,
            WorkflowRunControlAction.Resume,
            "WorkflowRunResumed",
            resumedStatus.ToString(),
            WorkflowRunControlPermissions.Pause,
            fingerprint,
            new Dictionary<string, string?>
            {
                ["previousRuntimeStatus"] = current.ToString(),
                ["resumedRuntimeStatus"] = resumedStatus.ToString()
            });
        await _database.SaveChangesAsync(cancellationToken);
        await ProcessAdvancedRunCoreAsync(workflowRunId, cancellationToken);
        return CreateControlResult(run, normalized.RequestId, WorkflowRunControlAction.Resume);
    }

    public Task<WorkflowRunControlResult> CancelRunAsync(
        Guid workflowRunId,
        WorkflowRunControlRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAdvancedRuntimeSerializedAsync(
            () => CancelRunCoreAsync(workflowRunId, request, cancellationToken),
            cancellationToken);

    private async Task<WorkflowRunControlResult> CancelRunCoreAsync(
        Guid workflowRunId,
        WorkflowRunControlRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeControlRequest(request);
        _controlAuthorizer.Demand(normalized.Actor, WorkflowRunControlPermissions.Cancel);
        var fingerprint = CreateControlFingerprint(
            WorkflowRunControlAction.Cancel,
            workflowRunId,
            normalized.Actor,
            normalized.Reason);
        var replay = await TryReplayControlAsync(
            workflowRunId,
            normalized.RequestId,
            WorkflowRunControlAction.Cancel,
            fingerprint,
            cancellationToken);
        if (replay is not null) return replay;

        await EnsureLegacySimulatorRuntimeRecordsAsync(cancellationToken);
        var run = await FindExecutionAsync(workflowRunId, cancellationToken);
        var current = WorkflowPersistence.ToExecutionSnapshot(run).RuntimeStatus;
        if (current is not WorkflowRuntimeStatus.Prepared and
            not WorkflowRuntimeStatus.Running and
            not WorkflowRuntimeStatus.Paused)
        {
            throw new WorkflowRunControlConflictException(
                current == WorkflowRuntimeStatus.Unknown
                    ? "Unknown work must be explicitly reconciled before the run can be terminated."
                    : $"A workflow run in '{current}' state cannot be cancelled safely.");
        }

        var hasUnsafeNode = await _database.WorkflowNodeExecutions.AnyAsync(
            item => item.WorkflowRunId == workflowRunId &&
                    (item.Status == WorkflowNodeExecutionStatus.Claimed.ToString() ||
                     item.Status == WorkflowNodeExecutionStatus.Running.ToString() ||
                     item.Status == WorkflowNodeExecutionStatus.Unknown.ToString()),
            cancellationToken);
        var hasUnsafeDeviceOperation = await _database.WorkflowDeviceOperations.AnyAsync(
            item => item.WorkflowRunId == workflowRunId &&
                    (item.Status == WorkflowDeviceOperationStatus.Accepted.ToString() ||
                     item.Status == WorkflowDeviceOperationStatus.Running.ToString() ||
                     item.Status == WorkflowDeviceOperationStatus.Unknown.ToString()),
            cancellationToken);
        if (hasUnsafeNode || hasUnsafeDeviceOperation)
        {
            throw new WorkflowRunControlConflictException(
                "The run still has active or unknown work. Pause future scheduling and finish reconciliation before cancelling.");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var cancellableNodes = await _database.WorkflowNodeExecutions
            .Where(item => item.WorkflowRunId == workflowRunId &&
                           (item.Status == WorkflowNodeExecutionStatus.Pending.ToString() ||
                            item.Status == WorkflowNodeExecutionStatus.Ready.ToString() ||
                            item.Status == WorkflowNodeExecutionStatus.WaitingForResource.ToString() ||
                            item.Status == WorkflowNodeExecutionStatus.WaitingForSignal.ToString() ||
                            item.Status == WorkflowNodeExecutionStatus.Blocked.ToString()))
            .ToListAsync(cancellationToken);
        foreach (var node in cancellableNodes)
        {
            node.Status = WorkflowNodeExecutionStatus.Cancelled.ToString();
            node.OutputJson = WorkflowPersistence.Serialize(CreateOutcomeSummary("Cancelled", normalized.Reason));
            node.CompletedAtUtc = now;
            node.UpdatedAtUtc = now;
        }

        run.RuntimeStatus = WorkflowRuntimeStatus.Cancelled.ToString();
        run.PendingStepJson = null;
        run.TransportOperationId = null;
        run.Attempt = 0;
        run.LastError = null;
        run.UpdatedAtUtc = now;
        AddControlAudit(
            run,
            normalized,
            WorkflowRunControlAction.Cancel,
            "WorkflowRunCancelled",
            WorkflowRuntimeStatus.Cancelled.ToString(),
            WorkflowRunControlPermissions.Cancel,
            fingerprint,
            new Dictionary<string, string?>
            {
                ["previousRuntimeStatus"] = current.ToString(),
                ["cancelledNodeCount"] = cancellableNodes.Count.ToString()
            });
        await _experimentRuntimeLeaseLifecycle.SynchronizeRunStateAsync(
            run,
            normalized.Actor,
            normalized.Reason,
            cancellationToken);
        await _database.SaveChangesAsync(cancellationToken);
        return CreateControlResult(run, normalized.RequestId, WorkflowRunControlAction.Cancel);
    }

    public Task<WorkflowRunControlResult> ResolveUnknownAsync(
        Guid workflowRunId,
        WorkflowUnknownResolutionRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAdvancedRuntimeSerializedAsync(
            () => ResolveUnknownCoreAsync(workflowRunId, request, cancellationToken),
            cancellationToken);

    private async Task<WorkflowRunControlResult> ResolveUnknownCoreAsync(
        Guid workflowRunId,
        WorkflowUnknownResolutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.NodeExecutionId == Guid.Empty)
            throw new ArgumentException("A node execution id is required.", nameof(request));
        if (!Enum.IsDefined(request.Outcome))
            throw new ArgumentOutOfRangeException(nameof(request), "A supported Unknown resolution outcome is required.");
        var normalized = NormalizeControlRequest(new WorkflowRunControlRequest
        {
            RequestId = request.RequestId,
            Actor = request.Actor,
            Reason = request.Reason
        });
        _controlAuthorizer.Demand(normalized.Actor, WorkflowRunControlPermissions.ResolveUnknown);
        var fingerprint = CreateControlFingerprint(
            WorkflowRunControlAction.ResolveUnknown,
            workflowRunId,
            normalized.Actor,
            normalized.Reason,
            request.NodeExecutionId,
            request.Outcome);
        var replay = await TryReplayControlAsync(
            workflowRunId,
            normalized.RequestId,
            WorkflowRunControlAction.ResolveUnknown,
            fingerprint,
            cancellationToken);
        if (replay is not null) return replay;

        var run = await FindExecutionAsync(workflowRunId, cancellationToken);
        var current = WorkflowPersistence.ToExecutionSnapshot(run).RuntimeStatus;
        if (current != WorkflowRuntimeStatus.Unknown)
        {
            throw new WorkflowRunControlConflictException(
                $"Only an Unknown workflow run can be resolved; current state is '{current}'.");
        }

        await EnsureLegacyUnknownRuntimeRecordsAsync(run, cancellationToken);

        var node = await _database.WorkflowNodeExecutions.SingleOrDefaultAsync(
            item => item.Id == request.NodeExecutionId && item.WorkflowRunId == workflowRunId,
            cancellationToken) ?? throw new KeyNotFoundException(
            $"Workflow node execution '{request.NodeExecutionId}' was not found in run '{workflowRunId}'.");
        if (!string.Equals(node.Status, WorkflowNodeExecutionStatus.Unknown.ToString(), StringComparison.Ordinal))
        {
            throw new WorkflowRunControlConflictException(
                $"Node execution '{node.Id}' is not Unknown and cannot be manually resolved.");
        }

        var completedStep = WorkflowPersistence.DeserializePendingStep(run.PendingStepJson) ??
                            throw new WorkflowRunControlConflictException(
                                "The Unknown workflow run no longer has the pending step required for reconciliation.");
        if (completedStep.NodeId != node.NodeId)
        {
            throw new WorkflowRunControlConflictException(
                "The selected Unknown node does not match the run's persisted pending step.");
        }

        var operation = await _database.WorkflowDeviceOperations
            .Where(item => item.NodeExecutionId == node.Id)
            .OrderByDescending(item => item.RequestedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (operation is not null &&
            !string.Equals(operation.Status, WorkflowDeviceOperationStatus.Unknown.ToString(), StringComparison.Ordinal))
        {
            throw new WorkflowRunControlConflictException(
                "The linked device operation is not Unknown and does not match this resolution request.");
        }

        var retainedNodeOutputs = WorkflowPersistence.DeserializeDetails(node.OutputJson);
        var retainedOperationOutputs = operation is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : WorkflowPersistence.DeserializeDetails(operation.ResultSummaryJson);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        WorkflowNextStepRequest? nextStep = null;
        WorkflowStepCompletionOutcome completionOutcome;
        if (request.Outcome == WorkflowUnknownResolutionOutcome.ConfirmedSucceeded)
        {
            completionOutcome = WorkflowStepCompletionOutcome.Succeeded;
            nextStep = WorkflowPersistence.ResolveFollowingStep(run, completedStep);
            run.CurrentNodeId = nextStep?.NodeId ?? completedStep.NodeId;
            run.PendingStepJson = nextStep is null ? null : WorkflowPersistence.Serialize(nextStep);
            run.TransportOperationId = null;
            run.Attempt = 0;
            run.LastError = null;
            run.RuntimeStatus = (nextStep is null
                ? WorkflowRuntimeStatus.Completed
                : WorkflowRuntimeStatus.Prepared).ToString();
        }
        else
        {
            completionOutcome = WorkflowStepCompletionOutcome.Failed;
            run.RuntimeStatus = WorkflowRuntimeStatus.Failed.ToString();
            run.LastError = normalized.Reason;
        }

        var operationId = operation?.OperationId ?? WorkflowPersistence.CreateStableOperationId(
            run.ExecutionId,
            node.NodeId,
            Math.Max(1, node.Attempt));
        await ApplyCompletionRuntimeRecordsAsync(
            run,
            completedStep,
            Math.Max(1, node.Attempt),
            new WorkflowStepCompletionRequest
            {
                TransportOperationId = operationId,
                Outcome = completionOutcome,
                Error = request.Outcome == WorkflowUnknownResolutionOutcome.ConfirmedFailed
                    ? normalized.Reason
                    : null
            },
            nextStep,
            now,
            cancellationToken);
        node.OutputJson = WorkflowPersistence.Serialize(new Dictionary<string, string?>(
            retainedNodeOutputs,
            StringComparer.OrdinalIgnoreCase)
        {
            ["outcome"] = completionOutcome.ToString(),
            ["error"] = request.Outcome == WorkflowUnknownResolutionOutcome.ConfirmedFailed
                ? normalized.Reason
                : null,
            ["resolution"] = request.Outcome.ToString(),
            ["resolvedBy"] = normalized.Actor,
            ["reason"] = normalized.Reason
        });
        if (operation is not null)
        {
            operation.ResultSummaryJson = WorkflowPersistence.Serialize(new Dictionary<string, string?>(
                retainedOperationOutputs,
                StringComparer.OrdinalIgnoreCase)
            {
                ["outcome"] = completionOutcome.ToString(),
                ["error"] = request.Outcome == WorkflowUnknownResolutionOutcome.ConfirmedFailed
                    ? normalized.Reason
                    : null,
                ["resolution"] = request.Outcome.ToString(),
                ["resolvedBy"] = normalized.Actor,
                ["reason"] = normalized.Reason
            });
        }

        run.UpdatedAtUtc = now;
        AddControlAudit(
            run,
            normalized,
            WorkflowRunControlAction.ResolveUnknown,
            "WorkflowUnknownResolved",
            run.RuntimeStatus ?? WorkflowRuntimeStatus.Unknown.ToString(),
            WorkflowRunControlPermissions.ResolveUnknown,
            fingerprint,
            new Dictionary<string, string?>
            {
                ["nodeExecutionId"] = node.Id.ToString(),
                ["nodeId"] = node.NodeId.ToString(),
                ["deviceOperationId"] = operation?.OperationId.ToString(),
                ["resolution"] = request.Outcome.ToString(),
                ["nextNodeId"] = nextStep?.NodeId.ToString()
            });
        await _experimentRuntimeLeaseLifecycle.SynchronizeRunStateAsync(
            run,
            normalized.Actor,
            normalized.Reason,
            cancellationToken);
        await _database.SaveChangesAsync(cancellationToken);
        if (completionOutcome == WorkflowStepCompletionOutcome.Succeeded)
        {
            await ProcessAdvancedRunCoreAsync(workflowRunId, cancellationToken);
        }
        return CreateControlResult(run, normalized.RequestId, WorkflowRunControlAction.ResolveUnknown);
    }

    private async Task EnsureLegacyUnknownRuntimeRecordsAsync(
        WorkflowExecutionRecord run,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(run.RuntimeStatus, WorkflowRuntimeStatus.Unknown.ToString(), StringComparison.Ordinal) ||
            await _database.WorkflowNodeExecutions.AnyAsync(
                item => item.WorkflowRunId == run.ExecutionId,
                cancellationToken))
        {
            return;
        }

        var step = WorkflowPersistence.DeserializePendingStep(run.PendingStepJson) ??
                   WorkflowPersistence.DeserializeResult(run.ResultJson).NextStepRequest;
        if (!IsSimulatorStep(step)) return;

        var attempt = Math.Max(1, run.Attempt);
        var operationId = run.TransportOperationId ?? WorkflowPersistence.CreateStableOperationId(
            run.ExecutionId,
            step!.NodeId,
            attempt);
        await ApplyCompletionRuntimeRecordsAsync(
            run,
            step!,
            attempt,
            new WorkflowStepCompletionRequest
            {
                TransportOperationId = operationId,
                Outcome = WorkflowStepCompletionOutcome.Unknown,
                Error = run.LastError
            },
            nextStep: null,
            run.UpdatedAtUtc ?? run.CreatedAtUtc,
            cancellationToken);
        await _database.SaveChangesAsync(cancellationToken);
    }

    private async Task<WorkflowRunControlResult?> TryReplayControlAsync(
        Guid workflowRunId,
        Guid requestId,
        WorkflowRunControlAction action,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var priorAudits = await _database.WorkflowAudits
            .AsNoTracking()
            .Where(item => item.RequestId == requestId)
            .OrderBy(item => item.OccurredAtUtc)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        if (priorAudits.Count == 0) return null;

        var controlAudit = priorAudits.FirstOrDefault(item => ControlEventTypes.Contains(
            item.EventType,
            StringComparer.Ordinal));
        if (controlAudit is null || controlAudit.ExecutionId != workflowRunId)
        {
            throw new WorkflowRunControlConflictException(
                $"Request id '{requestId}' has already been used for another operation.");
        }

        var details = WorkflowPersistence.DeserializeDetails(controlAudit.DetailsJson);
        if (!details.TryGetValue("controlAction", out var storedAction) ||
            !string.Equals(storedAction, action.ToString(), StringComparison.Ordinal) ||
            !details.TryGetValue("requestFingerprint", out var storedFingerprint) ||
            !string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new WorkflowRunControlConflictException(
                $"Request id '{requestId}' was reused with different control data.");
        }

        var run = await FindExecutionAsync(workflowRunId, cancellationToken);
        return CreateControlResult(run, requestId, action, isIdempotentReplay: true);
    }

    private void AddControlAudit(
        WorkflowExecutionRecord run,
        NormalizedControlRequest request,
        WorkflowRunControlAction action,
        string eventType,
        string outcome,
        string permission,
        string fingerprint,
        IReadOnlyDictionary<string, string?> actionDetails)
    {
        var persistedRequest = WorkflowPersistence.DeserializeRequest(run.RequestJson);
        var details = new Dictionary<string, string?>(actionDetails, StringComparer.OrdinalIgnoreCase)
        {
            ["controlAction"] = action.ToString(),
            ["requestFingerprint"] = fingerprint,
            ["requestId"] = request.RequestId.ToString(),
            ["permission"] = permission
        };
        _database.WorkflowAudits.Add(new WorkflowAuditRecord
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            Outcome = outcome,
            Reason = request.Reason,
            WorkflowId = run.WorkflowId,
            Version = run.Version,
            RequestId = request.RequestId,
            ExecutionId = run.ExecutionId,
            Actor = request.Actor,
            CorrelationId = persistedRequest.CorrelationId,
            DetailsJson = WorkflowPersistence.Serialize(details),
            OccurredAtUtc = _timeProvider.GetUtcNow().UtcDateTime
        });
    }

    private static WorkflowRunControlResult CreateControlResult(
        WorkflowExecutionRecord run,
        Guid requestId,
        WorkflowRunControlAction action,
        bool isIdempotentReplay = false) => new()
    {
        RequestId = requestId,
        WorkflowRunId = run.ExecutionId,
        Action = action,
        IsIdempotentReplay = isIdempotentReplay,
        Run = WorkflowPersistence.ToExecutionSnapshot(run)
    };

    private static NormalizedControlRequest NormalizeControlRequest(WorkflowRunControlRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty)
            throw new ArgumentException("A control request id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Actor))
            throw new ArgumentException("An operator identity is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("A control reason is required.", nameof(request));
        var actor = request.Actor.Trim();
        var reason = request.Reason.Trim();
        if (actor.Length > 256)
            throw new ArgumentException("The operator identity cannot exceed 256 characters.", nameof(request));
        if (reason.Length > 2048)
            throw new ArgumentException("The control reason cannot exceed 2048 characters.", nameof(request));
        return new NormalizedControlRequest(request.RequestId, actor, reason);
    }

    private static string CreateControlFingerprint(
        WorkflowRunControlAction action,
        Guid workflowRunId,
        string actor,
        string reason,
        Guid? nodeExecutionId = null,
        WorkflowUnknownResolutionOutcome? resolution = null)
    {
        var value = string.Join(
            '\u001f',
            action,
            workflowRunId.ToString("N"),
            actor,
            reason,
            nodeExecutionId?.ToString("N"),
            resolution?.ToString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private sealed record NormalizedControlRequest(Guid RequestId, string Actor, string Reason);
}
