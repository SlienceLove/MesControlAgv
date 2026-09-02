using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed partial class WorkflowApplicationService
{
    public async Task<IReadOnlyList<WorkflowNodeExecutionSnapshot>> ListNodeExecutionsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        if (workflowRunId == Guid.Empty) return [];

        var records = await _database.WorkflowNodeExecutions
            .AsNoTracking()
            .Where(item => item.WorkflowRunId == workflowRunId)
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Attempt)
            .ToListAsync(cancellationToken);
        if (records.Count > 0)
        {
            return records.Select(ToNodeExecutionSnapshot).ToArray();
        }

        var run = await _database.WorkflowExecutions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ExecutionId == workflowRunId, cancellationToken);
        if (run is null) return [];

        var step = WorkflowPersistence.DeserializePendingStep(run.PendingStepJson) ??
                   WorkflowPersistence.DeserializeResult(run.ResultJson).NextStepRequest;
        return step is null ? [] : [CreateLegacyNodeExecutionSnapshot(run, step)];
    }

    public async Task<IReadOnlyList<WorkflowDeviceOperationSnapshot>> ListDeviceOperationsAsync(
        Guid workflowRunId,
        CancellationToken cancellationToken)
    {
        if (workflowRunId == Guid.Empty) return [];

        var records = await _database.WorkflowDeviceOperations
            .AsNoTracking()
            .Where(item => item.WorkflowRunId == workflowRunId)
            .OrderBy(item => item.RequestedAtUtc)
            .ThenBy(item => item.OperationId)
            .ToListAsync(cancellationToken);
        if (records.Count > 0)
        {
            return records.Select(ToDeviceOperationSnapshot).ToArray();
        }

        var run = await _database.WorkflowExecutions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ExecutionId == workflowRunId, cancellationToken);
        if (run?.TransportOperationId is not { } operationId) return [];

        var step = WorkflowPersistence.DeserializePendingStep(run.PendingStepJson) ??
                   WorkflowPersistence.DeserializeResult(run.ResultJson).NextStepRequest;
        if (step is not { NodeType: WorkflowNodeType.Move }) return [];

        var attempt = Math.Max(1, run.Attempt);
        var nodeExecutionId = WorkflowPersistence.CreateStableRecordId(
            "node-execution",
            run.ExecutionId,
            step.NodeId,
            attempt);
        var request = WorkflowPersistence.DeserializeRequest(run.RequestJson);
        var updatedAt = AsOffset(run.UpdatedAtUtc ?? run.CreatedAtUtc);
        return
        [
            new WorkflowDeviceOperationSnapshot
            {
                OperationId = operationId,
                WorkflowRunId = run.ExecutionId,
                NodeExecutionId = nodeExecutionId,
                RequestId = run.RequestId,
                Attempt = attempt,
                CapabilityId = WorkflowCapabilityIds.AgvNavigateToStation,
                IdempotencyKey = operationId.ToString("N"),
                CorrelationId = request.CorrelationId,
                Status = ToLegacyDeviceStatus(WorkflowPersistence.ToExecutionSnapshot(run).RuntimeStatus),
                RequestSummary = CreateDeviceRequestSummary(step, nodeExecutionId),
                ResultSummary = CreateOutcomeSummary(run.RuntimeStatus, run.LastError),
                RequestedAt = updatedAt,
                CompletedAt = IsLegacyRunTerminal(run.RuntimeStatus) ? updatedAt : null,
                ReconciledAt = string.Equals(
                    run.RuntimeStatus,
                    WorkflowRuntimeStatus.Unknown.ToString(),
                    StringComparison.OrdinalIgnoreCase)
                    ? updatedAt
                    : null,
                LastError = run.LastError,
                UpdatedAt = updatedAt
            }
        ];
    }

    public async Task<IReadOnlyList<WorkflowRunTimelineEntry>> ListRunTimelineAsync(
        Guid workflowRunId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (workflowRunId == Guid.Empty) return [];

        var boundedLimit = Math.Clamp(limit <= 0 ? 200 : limit, 1, 500);
        var records = await _database.WorkflowAudits
            .AsNoTracking()
            .Where(item => item.ExecutionId == workflowRunId)
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenByDescending(item => item.Id)
            .Take(boundedLimit)
            .ToListAsync(cancellationToken);

        return records
            .OrderBy(item => item.OccurredAtUtc)
            .ThenBy(item => item.Id)
            .Select(ToTimelineEntry)
            .ToArray();
    }

    private WorkflowNodeExecutionRecord AddInitialNodeExecutionRecord(
        WorkflowExecutionRecord run,
        WorkflowNextStepRequest step,
        DateTime now)
    {
        var record = CreateNodeExecutionRecord(run, step, attempt: 1, now);
        _database.WorkflowNodeExecutions.Add(record);
        AddRuntimeAudit(run, "WorkflowNodePrepared", WorkflowNodeExecutionStatus.Ready.ToString(), null,
            new Dictionary<string, string?>
            {
                ["nodeExecutionId"] = record.Id.ToString(),
                ["nodeId"] = record.NodeId.ToString(),
                ["attempt"] = record.Attempt.ToString()
            });
        return record;
    }

    private async Task<(WorkflowNodeExecutionRecord Node, WorkflowDeviceOperationRecord? Device)>
        EnsureClaimRuntimeRecordsAsync(
            WorkflowExecutionRecord run,
            WorkflowNextStepRequest step,
            Guid operationId,
            int attempt,
            DateTime now,
            CancellationToken cancellationToken)
    {
        var node = await GetOrCreateNodeExecutionRecordAsync(
            run,
            step,
            Math.Max(1, attempt),
            now,
            cancellationToken);
        node.Status = WorkflowNodeExecutionStatus.Running.ToString();
        node.StartedAtUtc ??= now;
        node.LastError = null;
        node.UpdatedAtUtc = now;

        if (step.NodeType is not (WorkflowNodeType.Move or WorkflowNodeType.RobotProgram))
        {
            return (node, null);
        }

        var device = _database.WorkflowDeviceOperations.Local
            .FirstOrDefault(item => item.OperationId == operationId) ??
            await _database.WorkflowDeviceOperations
                .SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken);
        if (device is null)
        {
            var request = WorkflowPersistence.DeserializeRequest(run.RequestJson);
            device = new WorkflowDeviceOperationRecord
            {
                OperationId = operationId,
                WorkflowRunId = run.ExecutionId,
                NodeExecutionId = node.Id,
                RequestId = run.RequestId,
                Attempt = node.Attempt,
                CapabilityId = ResolveDeviceCapability(step),
                DeviceId = ResolveDeviceId(step),
                IdempotencyKey = operationId.ToString("N"),
                CorrelationId = request.CorrelationId,
                Status = WorkflowDeviceOperationStatus.Prepared.ToString(),
                RequestSummaryJson = WorkflowPersistence.Serialize(CreateDeviceRequestSummary(step, node.Id)),
                ResultSummaryJson = "{}",
                RequestedAtUtc = now,
                UpdatedAtUtc = now
            };
            _database.WorkflowDeviceOperations.Add(device);
        }

        return (node, device);
    }

    private async Task<(Guid NodeExecutionId, Guid? DeviceOperationId)> ApplyCompletionRuntimeRecordsAsync(
        WorkflowExecutionRecord run,
        WorkflowNextStepRequest completedStep,
        int completedAttempt,
        WorkflowStepCompletionRequest completion,
        WorkflowNextStepRequest? nextStep,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var node = await GetOrCreateNodeExecutionRecordAsync(
            run,
            completedStep,
            completedAttempt,
            now,
            cancellationToken);
        WorkflowDeviceOperationRecord? device = null;
        if (completedStep.NodeType is WorkflowNodeType.Move or WorkflowNodeType.RobotProgram)
        {
            (_, device) = await EnsureClaimRuntimeRecordsAsync(
                run,
                completedStep,
                completion.TransportOperationId,
                node.Attempt,
                node.StartedAtUtc ?? now,
                cancellationToken);
        }

        node.StartedAtUtc ??= now;
        node.Status = completion.Outcome switch
        {
            WorkflowStepCompletionOutcome.Succeeded => WorkflowNodeExecutionStatus.Succeeded.ToString(),
            WorkflowStepCompletionOutcome.Failed => WorkflowNodeExecutionStatus.Failed.ToString(),
            WorkflowStepCompletionOutcome.Unknown => WorkflowNodeExecutionStatus.Unknown.ToString(),
            WorkflowStepCompletionOutcome.Cancelled => WorkflowNodeExecutionStatus.Cancelled.ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(completion))
        };
        var outputs = new Dictionary<string, string?>(
            completion.Outputs ?? new Dictionary<string, string?>(),
            StringComparer.OrdinalIgnoreCase)
        {
            ["outcome"] = completion.Outcome.ToString(),
            ["error"] = string.IsNullOrWhiteSpace(completion.Error) ? null : completion.Error.Trim()
        };
        node.OutputJson = WorkflowPersistence.Serialize(outputs);
        node.LastError = string.IsNullOrWhiteSpace(completion.Error) ? null : completion.Error.Trim();
        node.CompletedAtUtc = completion.Outcome == WorkflowStepCompletionOutcome.Unknown ? null : now;
        node.UpdatedAtUtc = now;

        if (device is not null)
        {
            device.Status = completion.Outcome switch
            {
                WorkflowStepCompletionOutcome.Succeeded => WorkflowDeviceOperationStatus.Succeeded.ToString(),
                WorkflowStepCompletionOutcome.Failed => WorkflowDeviceOperationStatus.Failed.ToString(),
                WorkflowStepCompletionOutcome.Unknown => WorkflowDeviceOperationStatus.Unknown.ToString(),
                WorkflowStepCompletionOutcome.Cancelled => WorkflowDeviceOperationStatus.Cancelled.ToString(),
                _ => throw new ArgumentOutOfRangeException(nameof(completion))
            };
            device.ResultSummaryJson = WorkflowPersistence.Serialize(outputs);
            device.LastError = node.LastError;
            device.CompletedAtUtc = completion.Outcome == WorkflowStepCompletionOutcome.Unknown ? null : now;
            device.ReconciledAtUtc = now;
            device.UpdatedAtUtc = now;
        }

        if (completion.Outcome == WorkflowStepCompletionOutcome.Succeeded && nextStep is not null)
        {
            var existing = _database.WorkflowNodeExecutions.Local
                .FirstOrDefault(item => item.StepRequestId == nextStep.StepRequestId) ??
                await _database.WorkflowNodeExecutions.SingleOrDefaultAsync(
                    item => item.StepRequestId == nextStep.StepRequestId,
                    cancellationToken);
            if (existing is null)
            {
                var prepared = CreateNodeExecutionRecord(run, nextStep, attempt: 1, now);
                _database.WorkflowNodeExecutions.Add(prepared);
                AddRuntimeAudit(run, "WorkflowNodePrepared", WorkflowNodeExecutionStatus.Ready.ToString(), null,
                    new Dictionary<string, string?>
                    {
                        ["nodeExecutionId"] = prepared.Id.ToString(),
                        ["nodeId"] = prepared.NodeId.ToString(),
                        ["attempt"] = prepared.Attempt.ToString()
                    });
            }
        }

        return (node.Id, device?.OperationId);
    }

    private async Task<WorkflowNodeExecutionRecord> GetOrCreateNodeExecutionRecordAsync(
        WorkflowExecutionRecord run,
        WorkflowNextStepRequest step,
        int attempt,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var record = _database.WorkflowNodeExecutions.Local
            .FirstOrDefault(item => item.StepRequestId == step.StepRequestId) ??
            await _database.WorkflowNodeExecutions.SingleOrDefaultAsync(
                item => item.StepRequestId == step.StepRequestId,
                cancellationToken);
        if (record is not null) return record;

        record = CreateNodeExecutionRecord(run, step, attempt, now);
        record.Id = WorkflowPersistence.CreateStableRecordId(
            "node-execution",
            run.ExecutionId,
            step.NodeId,
            record.Attempt);
        _database.WorkflowNodeExecutions.Add(record);
        return record;
    }

    private static WorkflowNodeExecutionRecord CreateNodeExecutionRecord(
        WorkflowExecutionRecord run,
        WorkflowNextStepRequest step,
        int attempt,
        DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowRunId = run.ExecutionId,
        WorkflowId = run.WorkflowId,
        Version = run.Version,
        StepRequestId = step.StepRequestId,
        NodeId = step.NodeId,
        NodeTypeId = ResolveNodeTypeId(run, step),
        NodeName = step.NodeName,
        Attempt = Math.Max(1, attempt),
        Status = WorkflowNodeExecutionStatus.Ready.ToString(),
        InputJson = WorkflowPersistence.Serialize(CreateNodeInputs(step)),
        OutputJson = "{}",
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };

    private static WorkflowNodeExecutionSnapshot ToNodeExecutionSnapshot(
        WorkflowNodeExecutionRecord record) => new()
    {
        Id = record.Id,
        WorkflowRunId = record.WorkflowRunId,
        WorkflowId = record.WorkflowId,
        Version = record.Version,
        StepRequestId = record.StepRequestId,
        NodeId = record.NodeId,
        NodeTypeId = record.NodeTypeId,
        NodeName = record.NodeName,
        Attempt = record.Attempt,
        Status = Enum.TryParse<WorkflowNodeExecutionStatus>(record.Status, true, out var status)
            ? status
            : WorkflowNodeExecutionStatus.Unknown,
        Inputs = WorkflowPersistence.DeserializeDetails(record.InputJson),
        Outputs = WorkflowPersistence.DeserializeDetails(record.OutputJson),
        StartedAt = AsNullableOffset(record.StartedAtUtc),
        CompletedAt = AsNullableOffset(record.CompletedAtUtc),
        LastError = record.LastError,
        CreatedAt = AsOffset(record.CreatedAtUtc),
        UpdatedAt = AsOffset(record.UpdatedAtUtc)
    };

    private static WorkflowDeviceOperationSnapshot ToDeviceOperationSnapshot(
        WorkflowDeviceOperationRecord record) => new()
    {
        OperationId = record.OperationId,
        WorkflowRunId = record.WorkflowRunId,
        NodeExecutionId = record.NodeExecutionId,
        RequestId = record.RequestId,
        Attempt = record.Attempt,
        CapabilityId = record.CapabilityId,
        DeviceId = record.DeviceId,
        IdempotencyKey = record.IdempotencyKey,
        CorrelationId = record.CorrelationId,
        Status = Enum.TryParse<WorkflowDeviceOperationStatus>(record.Status, true, out var status)
            ? status
            : WorkflowDeviceOperationStatus.Unknown,
        RequestSummary = WorkflowPersistence.DeserializeDetails(record.RequestSummaryJson),
        ResultSummary = WorkflowPersistence.DeserializeDetails(record.ResultSummaryJson),
        RequestedAt = AsOffset(record.RequestedAtUtc),
        CompletedAt = AsNullableOffset(record.CompletedAtUtc),
        ReconciledAt = AsNullableOffset(record.ReconciledAtUtc),
        LastError = record.LastError,
        UpdatedAt = AsOffset(record.UpdatedAtUtc)
    };

    private static WorkflowRunTimelineEntry ToTimelineEntry(WorkflowAuditRecord record)
    {
        var details = WorkflowPersistence.DeserializeDetails(record.DetailsJson);
        return new WorkflowRunTimelineEntry
        {
            Id = record.Id,
            WorkflowRunId = record.ExecutionId ?? Guid.Empty,
            NodeExecutionId = ReadGuid(details, "nodeExecutionId"),
            DeviceOperationId = details.ContainsKey("deviceOperationId")
                ? ReadGuid(details, "deviceOperationId")
                : ReadGuid(details, "transportOperationId"),
            EventType = record.EventType,
            Outcome = record.Outcome,
            Code = record.Code,
            Reason = record.Reason,
            Actor = record.Actor,
            CorrelationId = record.CorrelationId,
            Details = details,
            OccurredAt = AsOffset(record.OccurredAtUtc)
        };
    }

    private static WorkflowNodeExecutionSnapshot CreateLegacyNodeExecutionSnapshot(
        WorkflowExecutionRecord run,
        WorkflowNextStepRequest step)
    {
        var attempt = Math.Max(1, run.Attempt);
        var status = ToLegacyNodeStatus(WorkflowPersistence.ToExecutionSnapshot(run).RuntimeStatus);
        var createdAt = AsOffset(run.CreatedAtUtc);
        var updatedAt = AsOffset(run.UpdatedAtUtc ?? run.CreatedAtUtc);
        return new WorkflowNodeExecutionSnapshot
        {
            Id = WorkflowPersistence.CreateStableRecordId(
                "node-execution",
                run.ExecutionId,
                step.NodeId,
                attempt),
            WorkflowRunId = run.ExecutionId,
            WorkflowId = run.WorkflowId,
            Version = run.Version,
            StepRequestId = step.StepRequestId,
            NodeId = step.NodeId,
            NodeTypeId = ResolveNodeTypeId(run, step),
            NodeName = step.NodeName,
            Attempt = attempt,
            Status = status,
            Inputs = CreateNodeInputs(step),
            Outputs = CreateOutcomeSummary(run.RuntimeStatus, run.LastError),
            StartedAt = status == WorkflowNodeExecutionStatus.Ready ? null : updatedAt,
            CompletedAt = status is WorkflowNodeExecutionStatus.Succeeded or
                WorkflowNodeExecutionStatus.Failed or WorkflowNodeExecutionStatus.Cancelled
                ? updatedAt
                : null,
            LastError = run.LastError,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt
        };
    }

    private static string ResolveNodeTypeId(
        WorkflowExecutionRecord run,
        WorkflowNextStepRequest step)
    {
        if (!string.IsNullOrWhiteSpace(run.DefinitionSnapshotJson))
        {
            var node = WorkflowPersistence.DeserializeDefinition(run.DefinitionSnapshotJson)
                .Nodes
                .FirstOrDefault(item => item.Id == step.NodeId);
            if (!string.IsNullOrWhiteSpace(node?.NodeTypeId)) return node.NodeTypeId;
        }

        if (!string.IsNullOrWhiteSpace(step.NodeTypeId)) return step.NodeTypeId;

        return step.NodeType switch
        {
            WorkflowNodeType.Start => WorkflowGraphNodeTypeIds.Start,
            WorkflowNodeType.End => WorkflowGraphNodeTypeIds.End,
            WorkflowNodeType.Move => WorkflowGraphNodeTypeIds.Move,
            WorkflowNodeType.Wait => WorkflowGraphNodeTypeIds.TimedWait,
            WorkflowNodeType.InstrumentOperation => WorkflowGraphNodeTypeIds.InstrumentReadStatus,
            WorkflowNodeType.RobotProgram => WorkflowGraphNodeTypeIds.RobotExecuteProgram,
            _ => $"legacy.{step.NodeType.ToString().ToLowerInvariant()}"
        };
    }

    private static IReadOnlyDictionary<string, string?> CreateNodeInputs(WorkflowNextStepRequest step)
    {
        var inputs = new Dictionary<string, string?>(step.Parameters, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(step.TargetStation))
        {
            inputs[WorkflowNodeConfigurationKeys.TargetStation] = step.TargetStation;
        }

        return inputs;
    }

    private static IReadOnlyDictionary<string, string?> CreateDeviceRequestSummary(
        WorkflowNextStepRequest step,
        Guid nodeExecutionId) => new Dictionary<string, string?>
    {
        ["nodeExecutionId"] = nodeExecutionId.ToString(),
        ["nodeId"] = step.NodeId.ToString(),
        ["stepRequestId"] = step.StepRequestId.ToString(),
        ["targetStation"] = step.TargetStation,
        ["deviceId"] = ResolveDeviceId(step),
        ["programName"] = ResolveProgramName(step)
    };

    private static string ResolveDeviceCapability(WorkflowNextStepRequest step) =>
        step.NodeType == WorkflowNodeType.RobotProgram
            ? WorkflowCapabilityIds.RobotExecuteProgram
            : WorkflowCapabilityIds.AgvNavigateToStation;

    private static string? ResolveDeviceId(WorkflowNextStepRequest step) =>
        step.Parameters.TryGetValue(WorkflowNodeConfigurationKeys.DeviceId, out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static string? ResolveProgramName(WorkflowNextStepRequest step) =>
        step.Parameters.TryGetValue(WorkflowNodeConfigurationKeys.ProgramName, out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static IReadOnlyDictionary<string, string?> CreateOutcomeSummary(
        string? outcome,
        string? error) => new Dictionary<string, string?>
    {
        ["outcome"] = outcome,
        ["error"] = string.IsNullOrWhiteSpace(error) ? null : error.Trim()
    };

    private static WorkflowNodeExecutionStatus ToLegacyNodeStatus(WorkflowRuntimeStatus status) => status switch
    {
        WorkflowRuntimeStatus.Prepared => WorkflowNodeExecutionStatus.Ready,
        WorkflowRuntimeStatus.Running => WorkflowNodeExecutionStatus.Running,
        WorkflowRuntimeStatus.Paused => WorkflowNodeExecutionStatus.Blocked,
        WorkflowRuntimeStatus.Completed => WorkflowNodeExecutionStatus.Succeeded,
        WorkflowRuntimeStatus.Failed => WorkflowNodeExecutionStatus.Failed,
        WorkflowRuntimeStatus.Unknown => WorkflowNodeExecutionStatus.Unknown,
        WorkflowRuntimeStatus.Cancelled => WorkflowNodeExecutionStatus.Cancelled,
        _ => WorkflowNodeExecutionStatus.Skipped
    };

    private static WorkflowDeviceOperationStatus ToLegacyDeviceStatus(WorkflowRuntimeStatus status) => status switch
    {
        WorkflowRuntimeStatus.Prepared => WorkflowDeviceOperationStatus.Prepared,
        WorkflowRuntimeStatus.Running => WorkflowDeviceOperationStatus.Running,
        WorkflowRuntimeStatus.Completed => WorkflowDeviceOperationStatus.Succeeded,
        WorkflowRuntimeStatus.Failed => WorkflowDeviceOperationStatus.Failed,
        WorkflowRuntimeStatus.Unknown => WorkflowDeviceOperationStatus.Unknown,
        WorkflowRuntimeStatus.Cancelled => WorkflowDeviceOperationStatus.Cancelled,
        _ => WorkflowDeviceOperationStatus.Unknown
    };

    private static bool IsLegacyRunTerminal(string? status) =>
        string.Equals(status, WorkflowRuntimeStatus.Completed.ToString(), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, WorkflowRuntimeStatus.Failed.ToString(), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, WorkflowRuntimeStatus.Cancelled.ToString(), StringComparison.OrdinalIgnoreCase);

    private static Guid? ReadGuid(IReadOnlyDictionary<string, string?> details, string key) =>
        details.TryGetValue(key, out var value) && Guid.TryParse(value, out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset AsOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? AsNullableOffset(DateTime? value) =>
        value is null ? null : AsOffset(value.Value);
}
