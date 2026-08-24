using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed partial class WorkflowApplicationService
{
    private static readonly SemaphoreSlim AdvancedRuntimeGate = new(1, 1);

    public async Task<IReadOnlyList<WorkflowRuntimeInteractionSnapshot>> ListRuntimeInteractionsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        if (workflowRunId == Guid.Empty) return [];
        return (await _database.WorkflowRuntimeInteractions
                .AsNoTracking()
                .Where(item => item.WorkflowRunId == workflowRunId)
                .OrderBy(item => item.ReceivedAtUtc)
                .ThenBy(item => item.RequestId)
                .ToListAsync(cancellationToken))
            .Select(ToInteractionSnapshot)
            .ToArray();
    }

    public async Task<WorkflowRuntimeInteractionResult> SubmitExternalSignalAsync(
        Guid workflowRunId,
        WorkflowExternalSignalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = NormalizeSignalRequest(request);
        _controlAuthorizer.Demand(normalized.Actor, WorkflowRunControlPermissions.SubmitSignal);
        var fingerprint = CreateSignalFingerprint(workflowRunId, normalized);

        await AdvancedRuntimeGate.WaitAsync(cancellationToken);
        try
        {
            var prior = await FindInteractionAsync(normalized.RequestId, cancellationToken);
            if (prior is not null)
            {
                return await ReplayInteractionAsync(prior, fingerprint, cancellationToken);
            }

            var run = await FindExecutionAsync(workflowRunId, cancellationToken);
            EnsureInteractionRunIsActive(run);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var interaction = new WorkflowRuntimeInteractionRecord
            {
                RequestId = normalized.RequestId,
                Fingerprint = fingerprint,
                WorkflowRunId = workflowRunId,
                InteractionType = WorkflowRuntimeInteractionType.ExternalSignal.ToString(),
                Status = WorkflowRuntimeInteractionStatus.Pending.ToString(),
                SignalName = normalized.SignalName,
                CorrelationValue = normalized.CorrelationValue,
                Actor = normalized.Actor,
                Reason = normalized.Reason,
                RequestJson = WorkflowPersistence.Serialize(normalized),
                DataJson = WorkflowPersistence.Serialize(normalized.Data),
                ReceivedAtUtc = now,
                UpdatedAtUtc = now
            };
            _database.WorkflowRuntimeInteractions.Add(interaction);
            AddAdvancedRuntimeAudit(
                run,
                "WorkflowExternalSignalReceived",
                WorkflowRuntimeInteractionStatus.Pending.ToString(),
                interaction.Actor,
                interaction.RequestId,
                null,
                interaction.Reason,
                interaction.CorrelationValue,
                new Dictionary<string, string?>
                {
                    ["interactionRequestId"] = interaction.RequestId.ToString(),
                    ["signalName"] = interaction.SignalName,
                    ["correlationValue"] = interaction.CorrelationValue
                });
            await _database.SaveChangesAsync(cancellationToken);
            await ProcessAdvancedRunCoreAsync(workflowRunId, cancellationToken);
            return CreateInteractionResult(interaction, run, isReplay: false);
        }
        finally
        {
            AdvancedRuntimeGate.Release();
        }
    }

    public async Task<WorkflowRuntimeInteractionResult> CompleteManualConfirmationAsync(
        Guid workflowRunId,
        Guid nodeExecutionId,
        WorkflowManualConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (nodeExecutionId == Guid.Empty)
            throw new ArgumentException("A node execution id is required.", nameof(nodeExecutionId));
        var normalized = NormalizeManualRequest(request);
        _controlAuthorizer.Demand(normalized.Actor, WorkflowRunControlPermissions.CompleteManualTask);
        var fingerprint = CreateManualFingerprint(workflowRunId, nodeExecutionId, normalized);

        await AdvancedRuntimeGate.WaitAsync(cancellationToken);
        try
        {
            var prior = await FindInteractionAsync(normalized.RequestId, cancellationToken);
            if (prior is not null)
            {
                return await ReplayInteractionAsync(prior, fingerprint, cancellationToken);
            }

            await ProcessAdvancedRunCoreAsync(workflowRunId, cancellationToken);
            var run = await FindExecutionAsync(workflowRunId, cancellationToken);
            EnsureInteractionRunIsActive(run);
            var node = await _database.WorkflowNodeExecutions.SingleOrDefaultAsync(
                item => item.Id == nodeExecutionId && item.WorkflowRunId == workflowRunId,
                cancellationToken) ?? throw new KeyNotFoundException(
                $"Workflow node execution '{nodeExecutionId}' was not found in run '{workflowRunId}'.");
            if (!IsNodeType(node, WorkflowGraphNodeTypeIds.ManualConfirmation) ||
                ParseNodeStatus(node.Status) != WorkflowNodeExecutionStatus.WaitingForSignal)
            {
                throw new WorkflowAdvancedRuntimeConflictException(
                    WorkflowAdvancedRuntimeCodes.ManualTaskNotWaiting,
                    "Only a waiting Manual Confirmation node can accept an operator outcome.");
            }

            var inputs = WorkflowPersistence.DeserializeDetails(node.InputJson);
            if (ReadBoolean(inputs, WorkflowNodeConfigurationKeys.RequireComment) &&
                string.IsNullOrWhiteSpace(normalized.Comment))
            {
                throw new WorkflowAdvancedRuntimeConflictException(
                    WorkflowAdvancedRuntimeCodes.ManualCommentRequired,
                    "This Manual Confirmation node requires a non-empty comment.");
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var interaction = new WorkflowRuntimeInteractionRecord
            {
                RequestId = normalized.RequestId,
                Fingerprint = fingerprint,
                WorkflowRunId = workflowRunId,
                NodeExecutionId = node.Id,
                InteractionType = WorkflowRuntimeInteractionType.ManualConfirmation.ToString(),
                Status = WorkflowRuntimeInteractionStatus.Applied.ToString(),
                Actor = normalized.Actor,
                Reason = normalized.Reason,
                RequestJson = WorkflowPersistence.Serialize(normalized),
                DataJson = WorkflowPersistence.Serialize(new Dictionary<string, string?>
                {
                    ["outcome"] = normalized.Outcome.ToString(),
                    ["comment"] = normalized.Comment
                }),
                ReceivedAtUtc = now,
                AppliedAtUtc = now,
                UpdatedAtUtc = now
            };
            _database.WorkflowRuntimeInteractions.Add(interaction);
            var edgeKind = normalized.Outcome == WorkflowManualConfirmationOutcome.Confirmed
                ? WorkflowEdgeKind.Success
                : WorkflowEdgeKind.Cancelled;
            await CompleteAdvancedNodeAsync(
                run,
                node,
                edgeKind,
                normalized.Outcome == WorkflowManualConfirmationOutcome.Confirmed
                    ? WorkflowNodeExecutionStatus.Succeeded
                    : WorkflowNodeExecutionStatus.Cancelled,
                new Dictionary<string, string?>
                {
                    ["confirmed"] = (normalized.Outcome == WorkflowManualConfirmationOutcome.Confirmed).ToString(),
                    ["confirmedBy"] = normalized.Actor,
                    ["confirmedAtUtc"] = FormatUtc(now),
                    ["comment"] = normalized.Comment
                },
                "WorkflowManualConfirmationCompleted",
                normalized.Actor,
                normalized.RequestId,
                normalized.Reason,
                cancellationToken);
            await _database.SaveChangesAsync(cancellationToken);
            await ProcessAdvancedRunCoreAsync(workflowRunId, cancellationToken);
            return CreateInteractionResult(interaction, run, isReplay: false);
        }
        finally
        {
            AdvancedRuntimeGate.Release();
        }
    }

    internal async Task ProcessAdvancedRuntimeAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        await AdvancedRuntimeGate.WaitAsync(cancellationToken);
        try
        {
            await ProcessAdvancedRunCoreAsync(workflowRunId, cancellationToken);
        }
        finally
        {
            AdvancedRuntimeGate.Release();
        }
    }

    private async Task<TResult> ExecuteAdvancedRuntimeSerializedAsync<TResult>(
        Func<Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        await AdvancedRuntimeGate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            AdvancedRuntimeGate.Release();
        }
    }

    private async Task ProcessAdvancedRunCoreAsync(Guid workflowRunId, CancellationToken cancellationToken)
    {
        var run = await _database.WorkflowExecutions.SingleOrDefaultAsync(
            item => item.ExecutionId == workflowRunId,
            cancellationToken);
        if (run is null || string.IsNullOrWhiteSpace(run.PendingStepJson)) return;
        var definition = ReadDefinitionSnapshot(run);
        var maxTransitions = Math.Max(1, (definition.Nodes ?? []).Count + 1);

        for (var transition = 0; transition < maxTransitions; transition++)
        {
            var runStatus = WorkflowPersistence.ToExecutionSnapshot(run).RuntimeStatus;
            if (runStatus is not (WorkflowRuntimeStatus.Prepared or
                WorkflowRuntimeStatus.Running or WorkflowRuntimeStatus.Paused)) return;
            var step = WorkflowPersistence.DeserializePendingStep(run.PendingStepJson);
            if (step is null) return;
            var node = await GetOrCreateNodeExecutionRecordAsync(
                run,
                step,
                Math.Max(1, run.Attempt),
                run.UpdatedAtUtc ?? run.CreatedAtUtc,
                cancellationToken);

            if (IsNodeType(node, WorkflowGraphNodeTypeIds.Condition))
            {
                if (runStatus == WorkflowRuntimeStatus.Paused &&
                    ParseNodeStatus(node.Status) == WorkflowNodeExecutionStatus.Ready) return;
                var advanced = await EvaluateConditionAsync(run, node, definition, cancellationToken);
                await _database.SaveChangesAsync(cancellationToken);
                if (!advanced) return;
                continue;
            }

            if (!IsNodeType(node, WorkflowGraphNodeTypeIds.ManualConfirmation) &&
                !IsNodeType(node, WorkflowGraphNodeTypeIds.SignalWait)) return;

            if (ParseNodeStatus(node.Status) == WorkflowNodeExecutionStatus.Ready)
            {
                if (runStatus == WorkflowRuntimeStatus.Paused) return;
                var activated = await ActivateInteractionWaitAsync(run, node, cancellationToken);
                await _database.SaveChangesAsync(cancellationToken);
                if (!activated) return;
            }

            if (ParseNodeStatus(node.Status) != WorkflowNodeExecutionStatus.WaitingForSignal) return;
            if (HasTimedOut(node))
            {
                await CompleteAdvancedNodeAsync(
                    run,
                    node,
                    WorkflowEdgeKind.Timeout,
                    WorkflowNodeExecutionStatus.TimedOut,
                    new Dictionary<string, string?>
                    {
                        ["timedOutAtUtc"] = FormatUtc(_timeProvider.GetUtcNow().UtcDateTime)
                    },
                    "WorkflowInteractionTimedOut",
                    "workflow-runtime",
                    null,
                    "The configured interaction timeout elapsed.",
                    cancellationToken);
                await _database.SaveChangesAsync(cancellationToken);
                continue;
            }

            if (IsNodeType(node, WorkflowGraphNodeTypeIds.SignalWait) &&
                await TryApplyPendingSignalAsync(run, node, cancellationToken))
            {
                await _database.SaveChangesAsync(cancellationToken);
                continue;
            }

            return;
        }

        throw new InvalidOperationException("Advanced workflow processing exceeded the pinned graph transition bound.");
    }

    private async Task<bool> EvaluateConditionAsync(
        WorkflowExecutionRecord run,
        WorkflowNodeExecutionRecord node,
        WorkflowDefinition definition,
        CancellationToken cancellationToken)
    {
        var edges = (definition.Edges ?? [])
            .Where(edge => edge.SourceNodeId == node.NodeId)
            .ToArray();
        var branches = edges
            .Where(edge => edge.Kind == WorkflowEdgeKind.ConditionTrue && edge.ConditionExpression is not null)
            .OrderBy(edge => edge.Priority)
            .ThenBy(edge => edge.Id)
            .ToArray();
        var inputs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        WorkflowEdgeDefinition? selected = null;

        for (var index = 0; index < branches.Length; index++)
        {
            var edge = branches[index];
            var expression = edge.ConditionExpression!;
            var source = await ResolveConditionValueAsync(run, expression, cancellationToken);
            var prefix = $"branch.{index}";
            inputs[$"{prefix}.edgeId"] = edge.Id.ToString();
            inputs[$"{prefix}.source"] = expression.Source.ToString();
            inputs[$"{prefix}.sourceNodeId"] = expression.SourceNodeId?.ToString();
            inputs[$"{prefix}.sourceKey"] = expression.SourceKey;
            inputs[$"{prefix}.valueType"] = expression.ValueType.ToString();
            inputs[$"{prefix}.operator"] = expression.Operator.ToString();
            inputs[$"{prefix}.compareValue"] = expression.CompareValue;
            inputs[$"{prefix}.sourceValue"] = source.Value;

            if (!source.Found)
            {
                inputs[$"{prefix}.result"] = "Missing";
                node.InputJson = WorkflowPersistence.Serialize(inputs);
                node.StartedAtUtc ??= _timeProvider.GetUtcNow().UtcDateTime;
                node.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
                if (expression.MissingValueBehavior == WorkflowConditionMissingValueBehavior.Wait)
                {
                    node.Status = WorkflowNodeExecutionStatus.WaitingForSignal.ToString();
                    if (!string.Equals(run.RuntimeStatus, WorkflowRuntimeStatus.Paused.ToString(), StringComparison.Ordinal))
                        run.RuntimeStatus = WorkflowRuntimeStatus.Running.ToString();
                    run.UpdatedAtUtc = node.UpdatedAtUtc;
                    AddAdvancedRuntimeAudit(
                        run,
                        "WorkflowConditionWaiting",
                        WorkflowNodeExecutionStatus.WaitingForSignal.ToString(),
                        "workflow-runtime",
                        null,
                        null,
                        $"Condition source '{expression.SourceKey}' has not been persisted.",
                        null,
                        new Dictionary<string, string?>
                        {
                            ["nodeExecutionId"] = node.Id.ToString(),
                            ["nodeId"] = node.NodeId.ToString(),
                            ["edgeId"] = edge.Id.ToString(),
                            ["sourceKey"] = expression.SourceKey
                        });
                    return false;
                }

                await FailAdvancedNodeAsync(
                    run,
                    node,
                    WorkflowAdvancedRuntimeCodes.ConditionValueMissing,
                    $"Condition source '{expression.SourceKey}' has not been persisted.",
                    inputs,
                    cancellationToken);
                return true;
            }

            if (!TryEvaluateCondition(expression, source.Value!, out var matched))
            {
                await FailAdvancedNodeAsync(
                    run,
                    node,
                    WorkflowAdvancedRuntimeCodes.ConditionValueMissing,
                    $"Condition source '{expression.SourceKey}' is not a valid '{expression.ValueType}' value.",
                    inputs,
                    cancellationToken);
                return true;
            }

            inputs[$"{prefix}.result"] = matched.ToString();
            if (!matched) continue;
            selected = edge;
            break;
        }

        selected ??= edges.SingleOrDefault(edge => edge.Kind == WorkflowEdgeKind.ConditionFalse);
        if (selected is null)
        {
            await FailAdvancedNodeAsync(
                run,
                node,
                WorkflowAdvancedRuntimeCodes.OutcomePathUnavailable,
                "The Condition gateway has no deterministic default edge.",
                inputs,
                cancellationToken);
            return true;
        }

        node.InputJson = WorkflowPersistence.Serialize(inputs);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await CompleteAdvancedNodeAsync(
            run,
            node,
            selected.Kind,
            WorkflowNodeExecutionStatus.Succeeded,
            new Dictionary<string, string?>
            {
                ["matchedEdgeId"] = selected.Id.ToString(),
                ["evaluatedAtUtc"] = FormatUtc(now)
            },
            "WorkflowConditionEvaluated",
            "workflow-runtime",
            null,
            null,
            cancellationToken,
            selected.Id);
        return true;
    }

    private async Task<bool> ActivateInteractionWaitAsync(
        WorkflowExecutionRecord run,
        WorkflowNodeExecutionRecord node,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        node.StartedAtUtc ??= now;
        node.UpdatedAtUtc = now;
        node.Status = WorkflowNodeExecutionStatus.WaitingForSignal.ToString();
        run.CurrentNodeId = node.NodeId;
        run.TransportOperationId = null;
        run.Attempt = Math.Max(1, node.Attempt);
        run.LastError = null;
        run.RuntimeStatus = WorkflowRuntimeStatus.Running.ToString();
        run.UpdatedAtUtc = now;

        if (IsNodeType(node, WorkflowGraphNodeTypeIds.SignalWait))
        {
            var inputs = new Dictionary<string, string?>(
                WorkflowPersistence.DeserializeDetails(node.InputJson),
                StringComparer.OrdinalIgnoreCase);
            var correlationKey = ReadRequired(inputs, WorkflowNodeConfigurationKeys.CorrelationKey);
            var execution = WorkflowPersistence.DeserializeRequest(run.RequestJson);
            if (!TryGetValue(execution.Parameters, correlationKey, out var correlationValue) ||
                string.IsNullOrWhiteSpace(correlationValue))
            {
                await FailAdvancedNodeAsync(
                    run,
                    node,
                    WorkflowAdvancedRuntimeCodes.SignalCorrelationMissing,
                    $"Run input '{correlationKey}' required for signal correlation is missing.",
                    inputs,
                    cancellationToken);
                return false;
            }

            inputs["correlationValue"] = correlationValue.Trim();
            node.InputJson = WorkflowPersistence.Serialize(inputs);
        }

        AddAdvancedRuntimeAudit(
            run,
            IsNodeType(node, WorkflowGraphNodeTypeIds.SignalWait)
                ? "WorkflowExternalSignalWaitStarted"
                : "WorkflowManualConfirmationRequested",
            WorkflowNodeExecutionStatus.WaitingForSignal.ToString(),
            "workflow-runtime",
            null,
            null,
            null,
            null,
            new Dictionary<string, string?>
            {
                ["nodeExecutionId"] = node.Id.ToString(),
                ["nodeId"] = node.NodeId.ToString(),
                ["timeoutSeconds"] = ReadOptional(
                    WorkflowPersistence.DeserializeDetails(node.InputJson),
                    WorkflowNodeConfigurationKeys.TimeoutSeconds)
            });
        return true;
    }

    private async Task<bool> TryApplyPendingSignalAsync(
        WorkflowExecutionRecord run,
        WorkflowNodeExecutionRecord node,
        CancellationToken cancellationToken)
    {
        var inputs = WorkflowPersistence.DeserializeDetails(node.InputJson);
        var signalName = ReadRequired(inputs, WorkflowNodeConfigurationKeys.SignalName);
        var correlationValue = ReadRequired(inputs, "correlationValue");
        var interaction = await _database.WorkflowRuntimeInteractions
            .Where(item => item.WorkflowRunId == run.ExecutionId &&
                           item.InteractionType == WorkflowRuntimeInteractionType.ExternalSignal.ToString() &&
                           item.Status == WorkflowRuntimeInteractionStatus.Pending.ToString() &&
                           item.SignalName == signalName &&
                           item.CorrelationValue == correlationValue)
            .OrderBy(item => item.ReceivedAtUtc)
            .ThenBy(item => item.RequestId)
            .FirstOrDefaultAsync(cancellationToken);
        if (interaction is null) return false;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        interaction.NodeExecutionId = node.Id;
        interaction.Status = WorkflowRuntimeInteractionStatus.Applied.ToString();
        interaction.AppliedAtUtc = now;
        interaction.UpdatedAtUtc = now;
        await CompleteAdvancedNodeAsync(
            run,
            node,
            WorkflowEdgeKind.Success,
            WorkflowNodeExecutionStatus.Succeeded,
            new Dictionary<string, string?>
            {
                ["signalId"] = interaction.RequestId.ToString(),
                ["signalName"] = interaction.SignalName,
                ["receivedAtUtc"] = FormatUtc(interaction.ReceivedAtUtc)
            },
            "WorkflowExternalSignalApplied",
            interaction.Actor,
            interaction.RequestId,
            interaction.Reason,
            cancellationToken);
        return true;
    }

    private async Task CompleteAdvancedNodeAsync(
        WorkflowExecutionRecord run,
        WorkflowNodeExecutionRecord node,
        WorkflowEdgeKind edgeKind,
        WorkflowNodeExecutionStatus nodeStatus,
        IReadOnlyDictionary<string, string?> outputs,
        string eventType,
        string actor,
        Guid? interactionRequestId,
        string? reason,
        CancellationToken cancellationToken,
        Guid? selectedEdgeId = null)
    {
        var step = WorkflowPersistence.DeserializePendingStep(run.PendingStepJson) ??
                   throw new InvalidOperationException("The advanced node has no durable pending step.");
        WorkflowNextStepRequest? nextStep;
        try
        {
            nextStep = WorkflowPersistence.ResolveFollowingStep(run, step, edgeKind, selectedEdgeId);
        }
        catch (InvalidOperationException exception)
        {
            await FailAdvancedNodeAsync(
                run,
                node,
                WorkflowAdvancedRuntimeCodes.OutcomePathUnavailable,
                exception.Message,
                outputs,
                cancellationToken);
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        node.StartedAtUtc ??= now;
        node.Status = nodeStatus.ToString();
        node.OutputJson = WorkflowPersistence.Serialize(outputs);
        node.LastError = null;
        node.CompletedAtUtc = now;
        node.UpdatedAtUtc = now;
        var preservePause = string.Equals(
            run.RuntimeStatus,
            WorkflowRuntimeStatus.Paused.ToString(),
            StringComparison.Ordinal);
        run.CurrentNodeId = nextStep?.NodeId ?? node.NodeId;
        run.PendingStepJson = nextStep is null ? null : WorkflowPersistence.Serialize(nextStep);
        run.TransportOperationId = null;
        run.Attempt = 0;
        run.LastError = null;
        run.RuntimeStatus = (nextStep is null
            ? WorkflowRuntimeStatus.Completed
            : preservePause
                ? WorkflowRuntimeStatus.Paused
                : WorkflowRuntimeStatus.Prepared).ToString();
        run.UpdatedAtUtc = now;

        Guid? nextNodeExecutionId = null;
        if (nextStep is not null)
        {
            var prepared = CreateNodeExecutionRecord(run, nextStep, attempt: 1, now);
            _database.WorkflowNodeExecutions.Add(prepared);
            nextNodeExecutionId = prepared.Id;
            AddRuntimeAudit(run, "WorkflowNodePrepared", WorkflowNodeExecutionStatus.Ready.ToString(), null,
                new Dictionary<string, string?>
                {
                    ["nodeExecutionId"] = prepared.Id.ToString(),
                    ["nodeId"] = prepared.NodeId.ToString(),
                    ["attempt"] = prepared.Attempt.ToString()
                });
        }

        AddAdvancedRuntimeAudit(
            run,
            eventType,
            nodeStatus.ToString(),
            actor,
            interactionRequestId,
            null,
            reason,
            null,
            new Dictionary<string, string?>
            {
                ["nodeExecutionId"] = node.Id.ToString(),
                ["nodeId"] = node.NodeId.ToString(),
                ["edgeKind"] = edgeKind.ToString(),
                ["selectedEdgeId"] = selectedEdgeId?.ToString(),
                ["nextNodeId"] = nextStep?.NodeId.ToString(),
                ["nextNodeExecutionId"] = nextNodeExecutionId?.ToString(),
                ["interactionRequestId"] = interactionRequestId?.ToString()
            });
        await _experimentRuntimeLeaseLifecycle.SynchronizeRunStateAsync(
            run,
            actor,
            reason ?? $"Advanced node completed with '{nodeStatus}'.",
            cancellationToken);
    }

    private async Task FailAdvancedNodeAsync(
        WorkflowExecutionRecord run,
        WorkflowNodeExecutionRecord node,
        string code,
        string reason,
        IReadOnlyDictionary<string, string?> outputs,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        node.StartedAtUtc ??= now;
        node.Status = WorkflowNodeExecutionStatus.Failed.ToString();
        node.OutputJson = WorkflowPersistence.Serialize(outputs);
        node.LastError = reason;
        node.CompletedAtUtc = now;
        node.UpdatedAtUtc = now;
        run.CurrentNodeId = node.NodeId;
        run.PendingStepJson = null;
        run.TransportOperationId = null;
        run.Attempt = 0;
        run.LastError = reason;
        run.RuntimeStatus = WorkflowRuntimeStatus.Failed.ToString();
        run.UpdatedAtUtc = now;
        AddAdvancedRuntimeAudit(
            run,
            "WorkflowAdvancedNodeFailed",
            WorkflowRuntimeStatus.Failed.ToString(),
            "workflow-runtime",
            null,
            code,
            reason,
            null,
            new Dictionary<string, string?>
            {
                ["nodeExecutionId"] = node.Id.ToString(),
                ["nodeId"] = node.NodeId.ToString()
            });
        await _experimentRuntimeLeaseLifecycle.SynchronizeRunStateAsync(
            run,
            "workflow-runtime",
            reason,
            cancellationToken);
    }

    private async Task<ConditionValueResolution> ResolveConditionValueAsync(
        WorkflowExecutionRecord run,
        WorkflowConditionExpression expression,
        CancellationToken cancellationToken)
    {
        if (expression.Source == WorkflowConditionValueSource.RunInput)
        {
            var request = WorkflowPersistence.DeserializeRequest(run.RequestJson);
            return TryGetValue(request.Parameters, expression.SourceKey, out var value) && value is not null
                ? new ConditionValueResolution(true, value)
                : new ConditionValueResolution(false, null);
        }

        if (expression.Source == WorkflowConditionValueSource.NodeOutput &&
            expression.SourceNodeId is { } sourceNodeId)
        {
            var record = await _database.WorkflowNodeExecutions
                .AsNoTracking()
                .Where(item => item.WorkflowRunId == run.ExecutionId &&
                               item.NodeId == sourceNodeId &&
                               item.Status == WorkflowNodeExecutionStatus.Succeeded.ToString())
                .OrderByDescending(item => item.Attempt)
                .ThenByDescending(item => item.CompletedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (record is not null)
            {
                var outputs = WorkflowPersistence.DeserializeDetails(record.OutputJson);
                if (TryGetValue(outputs, expression.SourceKey, out var value) && value is not null)
                    return new ConditionValueResolution(true, value);
            }
        }

        return new ConditionValueResolution(false, null);
    }

    private bool HasTimedOut(WorkflowNodeExecutionRecord node)
    {
        if (node.StartedAtUtc is not { } startedAt) return false;
        var inputs = WorkflowPersistence.DeserializeDetails(node.InputJson);
        return decimal.TryParse(
                   ReadOptional(inputs, WorkflowNodeConfigurationKeys.TimeoutSeconds),
                   NumberStyles.Number,
                   CultureInfo.InvariantCulture,
                   out var seconds) &&
               seconds >= 0 &&
               _timeProvider.GetUtcNow().UtcDateTime - startedAt >= TimeSpan.FromSeconds((double)seconds);
    }

    private static bool TryEvaluateCondition(
        WorkflowConditionExpression expression,
        string sourceValue,
        out bool result)
    {
        if (!TryCompare(expression.ValueType, sourceValue, expression.CompareValue, out var comparison))
        {
            result = false;
            return false;
        }

        result = expression.Operator switch
        {
            WorkflowConditionOperator.Equal => comparison == 0,
            WorkflowConditionOperator.NotEqual => comparison != 0,
            WorkflowConditionOperator.LessThan => comparison < 0,
            WorkflowConditionOperator.LessThanOrEqual => comparison <= 0,
            WorkflowConditionOperator.GreaterThan => comparison > 0,
            WorkflowConditionOperator.GreaterThanOrEqual => comparison >= 0,
            _ => false
        };
        return expression.Operator != WorkflowConditionOperator.Unspecified;
    }

    private static bool TryCompare(
        WorkflowSchemaValueType valueType,
        string left,
        string right,
        out int comparison)
    {
        comparison = 0;
        switch (valueType)
        {
            case WorkflowSchemaValueType.String:
                comparison = string.Compare(left, right, StringComparison.Ordinal);
                return true;
            case WorkflowSchemaValueType.Integer:
                if (long.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var leftInteger) &&
                    long.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rightInteger))
                {
                    comparison = leftInteger.CompareTo(rightInteger);
                    return true;
                }
                return false;
            case WorkflowSchemaValueType.Decimal:
                if (decimal.TryParse(left, NumberStyles.Number, CultureInfo.InvariantCulture, out var leftDecimal) &&
                    decimal.TryParse(right, NumberStyles.Number, CultureInfo.InvariantCulture, out var rightDecimal))
                {
                    comparison = leftDecimal.CompareTo(rightDecimal);
                    return true;
                }
                return false;
            case WorkflowSchemaValueType.Boolean:
                if (bool.TryParse(left, out var leftBoolean) && bool.TryParse(right, out var rightBoolean))
                {
                    comparison = leftBoolean.CompareTo(rightBoolean);
                    return true;
                }
                return false;
            case WorkflowSchemaValueType.DateTimeOffset:
                if (DateTimeOffset.TryParse(left, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var leftDate) &&
                    DateTimeOffset.TryParse(right, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var rightDate))
                {
                    comparison = leftDate.CompareTo(rightDate);
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private async Task<WorkflowRuntimeInteractionRecord?> FindInteractionAsync(
        Guid requestId,
        CancellationToken cancellationToken) =>
        requestId == Guid.Empty
            ? null
            : await _database.WorkflowRuntimeInteractions.SingleOrDefaultAsync(
                item => item.RequestId == requestId,
                cancellationToken);

    private async Task<WorkflowRuntimeInteractionResult> ReplayInteractionAsync(
        WorkflowRuntimeInteractionRecord interaction,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(interaction.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new WorkflowAdvancedRuntimeConflictException(
                WorkflowAdvancedRuntimeCodes.RequestIdReused,
                "The interaction request id has already been used for a different payload.");
        }

        var run = await FindExecutionAsync(interaction.WorkflowRunId, cancellationToken);
        return CreateInteractionResult(interaction, run, isReplay: true);
    }

    private static WorkflowRuntimeInteractionResult CreateInteractionResult(
        WorkflowRuntimeInteractionRecord interaction,
        WorkflowExecutionRecord run,
        bool isReplay) => new()
    {
        RequestId = interaction.RequestId,
        WorkflowRunId = interaction.WorkflowRunId,
        NodeExecutionId = interaction.NodeExecutionId,
        InteractionType = ParseInteractionType(interaction.InteractionType),
        Status = ParseInteractionStatus(interaction.Status),
        IsIdempotentReplay = isReplay,
        ReceivedAt = AsUtcOffset(interaction.ReceivedAtUtc),
        AppliedAt = interaction.AppliedAtUtc is { } applied ? AsUtcOffset(applied) : null,
        Run = WorkflowPersistence.ToExecutionSnapshot(run)
    };

    private static WorkflowRuntimeInteractionSnapshot ToInteractionSnapshot(
        WorkflowRuntimeInteractionRecord interaction) => new()
    {
        RequestId = interaction.RequestId,
        WorkflowRunId = interaction.WorkflowRunId,
        NodeExecutionId = interaction.NodeExecutionId,
        InteractionType = ParseInteractionType(interaction.InteractionType),
        Status = ParseInteractionStatus(interaction.Status),
        SignalName = interaction.SignalName,
        CorrelationValue = interaction.CorrelationValue,
        Actor = interaction.Actor,
        Reason = interaction.Reason,
        Data = WorkflowPersistence.DeserializeDetails(interaction.DataJson),
        ReceivedAt = AsUtcOffset(interaction.ReceivedAtUtc),
        AppliedAt = interaction.AppliedAtUtc is { } applied ? AsUtcOffset(applied) : null,
        UpdatedAt = AsUtcOffset(interaction.UpdatedAtUtc)
    };

    private void AddAdvancedRuntimeAudit(
        WorkflowExecutionRecord run,
        string eventType,
        string outcome,
        string actor,
        Guid? requestId,
        string? code,
        string? reason,
        string? correlationId,
        IReadOnlyDictionary<string, string?> details) =>
        _database.WorkflowAudits.Add(new WorkflowAuditRecord
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            Outcome = outcome,
            Code = code,
            Reason = reason,
            WorkflowId = run.WorkflowId,
            Version = run.Version,
            RequestId = requestId ?? run.RequestId,
            ExecutionId = run.ExecutionId,
            Actor = actor,
            CorrelationId = correlationId,
            DetailsJson = WorkflowPersistence.Serialize(details),
            OccurredAtUtc = _timeProvider.GetUtcNow().UtcDateTime
        });

    private static WorkflowDefinition ReadDefinitionSnapshot(WorkflowExecutionRecord run) =>
        string.IsNullOrWhiteSpace(run.DefinitionSnapshotJson)
            ? throw new InvalidOperationException("The workflow run has no immutable definition snapshot.")
            : WorkflowPersistence.DeserializeDefinition(run.DefinitionSnapshotJson);

    private static void EnsureInteractionRunIsActive(WorkflowExecutionRecord run)
    {
        var status = WorkflowPersistence.ToExecutionSnapshot(run).RuntimeStatus;
        if (status is WorkflowRuntimeStatus.Prepared or WorkflowRuntimeStatus.Running or WorkflowRuntimeStatus.Paused)
            return;
        throw new WorkflowAdvancedRuntimeConflictException(
            WorkflowAdvancedRuntimeCodes.RunNotActive,
            $"A workflow run in '{status}' state cannot accept a runtime interaction.");
    }

    private static WorkflowExternalSignalRequest NormalizeSignalRequest(WorkflowExternalSignalRequest request)
    {
        if (request.RequestId == Guid.Empty)
            throw new ArgumentException("A non-empty interaction request id is required.", nameof(request));
        return request with
        {
            Actor = RequireInteractionText(request.Actor, "actor", 256),
            Reason = RequireInteractionText(request.Reason, "reason", 2048),
            SignalName = RequireInteractionText(request.SignalName, "signal name", 128),
            CorrelationValue = RequireInteractionText(request.CorrelationValue, "correlation value", 256),
            Data = NormalizeInteractionData(request.Data)
        };
    }

    private static WorkflowManualConfirmationRequest NormalizeManualRequest(
        WorkflowManualConfirmationRequest request)
    {
        if (request.RequestId == Guid.Empty)
            throw new ArgumentException("A non-empty interaction request id is required.", nameof(request));
        if (!Enum.IsDefined(request.Outcome))
            throw new ArgumentOutOfRangeException(nameof(request), "A supported manual outcome is required.");
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (comment?.Length > 2048)
            throw new ArgumentException("The manual comment cannot exceed 2048 characters.", nameof(request));
        return request with
        {
            Actor = RequireInteractionText(request.Actor, "actor", 256),
            Reason = RequireInteractionText(request.Reason, "reason", 2048),
            Comment = comment
        };
    }

    private static IReadOnlyDictionary<string, string?> NormalizeInteractionData(
        IReadOnlyDictionary<string, string?>? data)
    {
        if ((data?.Count ?? 0) > 64)
            throw new ArgumentException("External signal data cannot contain more than 64 fields.", nameof(data));
        var normalized = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data ?? new Dictionary<string, string?>())
        {
            var key = RequireInteractionText(item.Key, "data key", 128);
            var value = item.Value?.Trim();
            if (value?.Length > 2048)
                throw new ArgumentException($"External signal data '{key}' exceeds 2048 characters.", nameof(data));
            if (!normalized.TryAdd(key, value))
                throw new ArgumentException($"External signal data key '{key}' is duplicated.", nameof(data));
        }
        return normalized;
    }

    private static string CreateSignalFingerprint(Guid workflowRunId, WorkflowExternalSignalRequest request)
    {
        var data = string.Join('\u001e', request.Data
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key.Length}:{item.Key}={item.Value?.Length ?? -1}:{item.Value}"));
        return HashFingerprint(string.Join(
            '\u001f',
            WorkflowRuntimeInteractionType.ExternalSignal,
            workflowRunId,
            request.Actor,
            request.Reason,
            request.SignalName,
            request.CorrelationValue,
            data));
    }

    private static string CreateManualFingerprint(
        Guid workflowRunId,
        Guid nodeExecutionId,
        WorkflowManualConfirmationRequest request) =>
        HashFingerprint(string.Join(
            '\u001f',
            WorkflowRuntimeInteractionType.ManualConfirmation,
            workflowRunId,
            nodeExecutionId,
            request.Actor,
            request.Reason,
            request.Outcome,
            request.Comment));

    private static string HashFingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string RequireInteractionText(string? value, string label, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"A non-empty {label} is required.");
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
            throw new ArgumentException($"The {label} cannot exceed {maximumLength} characters.");
        return normalized;
    }

    private static string ReadRequired(IReadOnlyDictionary<string, string?> values, string key) =>
        TryGetValue(values, key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException($"Required advanced runtime input '{key}' is missing.");

    private static string? ReadOptional(IReadOnlyDictionary<string, string?> values, string key) =>
        TryGetValue(values, key, out var value) ? value : null;

    private static bool ReadBoolean(IReadOnlyDictionary<string, string?> values, string key) =>
        bool.TryParse(ReadOptional(values, key), out var result) && result;

    private static bool TryGetValue(
        IReadOnlyDictionary<string, string?>? values,
        string key,
        out string? value)
    {
        foreach (var item in values ?? new Dictionary<string, string?>())
        {
            if (!string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            value = item.Value;
            return true;
        }
        value = null;
        return false;
    }

    private static bool IsNodeType(WorkflowNodeExecutionRecord node, string nodeTypeId) =>
        string.Equals(node.NodeTypeId, nodeTypeId, StringComparison.OrdinalIgnoreCase);

    private static WorkflowRuntimeInteractionType ParseInteractionType(string value) =>
        Enum.TryParse<WorkflowRuntimeInteractionType>(value, true, out var result)
            ? result
            : throw new InvalidOperationException($"Unknown workflow interaction type '{value}'.");

    private static WorkflowRuntimeInteractionStatus ParseInteractionStatus(string value) =>
        Enum.TryParse<WorkflowRuntimeInteractionStatus>(value, true, out var result)
            ? result
            : throw new InvalidOperationException($"Unknown workflow interaction status '{value}'.");

    private static string FormatUtc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset AsUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record ConditionValueResolution(bool Found, string? Value);
}

public sealed class WorkflowAdvancedRuntimeConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Device-free coordinator for durable conditions and human/external waits.</summary>
public sealed class WorkflowAdvancedRuntimeWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<WorkflowAdvancedRuntimeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Guid[] runIds;
                using (var candidateScope = scopeFactory.CreateScope())
                {
                    var database = candidateScope.ServiceProvider.GetRequiredService<MesDbContext>();
                    runIds = await database.WorkflowExecutions
                        .AsNoTracking()
                        .Where(item => item.PendingStepJson != null &&
                                       (item.RuntimeStatus == WorkflowRuntimeStatus.Prepared.ToString() ||
                                        item.RuntimeStatus == WorkflowRuntimeStatus.Running.ToString() ||
                                        item.RuntimeStatus == WorkflowRuntimeStatus.Paused.ToString()))
                        .OrderBy(item => item.UpdatedAtUtc)
                        .Select(item => item.ExecutionId)
                        .ToArrayAsync(stoppingToken);
                }

                foreach (var runId in runIds)
                {
                    try
                    {
                        using var runScope = scopeFactory.CreateScope();
                        await runScope.ServiceProvider
                            .GetRequiredService<WorkflowApplicationService>()
                            .ProcessAdvancedRuntimeAsync(runId, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(
                            exception,
                            "Advanced workflow runtime coordination failed for run {WorkflowRunId}.",
                            runId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Advanced workflow runtime candidate polling failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
