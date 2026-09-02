using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed partial class WorkflowApplicationService
{
    public async Task<IReadOnlyList<WorkflowNodeExecutionWorkItem>> ListSimulatorDispatchableNodesAsync(
        CancellationToken cancellationToken)
    {
        await EnsureLegacySimulatorRuntimeRecordsAsync(cancellationToken);
        var records = await (
                from node in _database.WorkflowNodeExecutions.AsNoTracking()
                join run in _database.WorkflowExecutions.AsNoTracking()
                    on node.WorkflowRunId equals run.ExecutionId
                where (run.RuntimeStatus == WorkflowRuntimeStatus.Prepared.ToString() ||
                       run.RuntimeStatus == WorkflowRuntimeStatus.Running.ToString()) &&
                      node.Status == WorkflowNodeExecutionStatus.Ready.ToString() &&
                      (node.NodeTypeId == WorkflowGraphNodeTypeIds.Move ||
                       node.NodeTypeId == WorkflowGraphNodeTypeIds.TimedWait ||
                       node.NodeTypeId == WorkflowGraphNodeTypeIds.Wait)
                select node)
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return (await CreateWorkItemsAsync(records, cancellationToken))
            .Where(IsSimulatorWorkItem)
            .ToArray();
    }

    public async Task<IReadOnlyList<WorkflowNodeExecutionWorkItem>> ListSimulatorRecoverableNodesAsync(
        CancellationToken cancellationToken)
    {
        await EnsureLegacySimulatorRuntimeRecordsAsync(cancellationToken);
        var records = await _database.WorkflowNodeExecutions
            .AsNoTracking()
            .Where(item => item.Status == WorkflowNodeExecutionStatus.Running.ToString() &&
                           (item.NodeTypeId == WorkflowGraphNodeTypeIds.Move ||
                            item.NodeTypeId == WorkflowGraphNodeTypeIds.TimedWait ||
                            item.NodeTypeId == WorkflowGraphNodeTypeIds.Wait))
            .OrderBy(item => item.StartedAtUtc)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return (await CreateWorkItemsAsync(records, cancellationToken))
            .Where(IsSimulatorWorkItem)
            .ToArray();
    }

    public async Task<IReadOnlyList<WorkflowNodeExecutionWorkItem>> ListAuboProgramDispatchableNodesAsync(
        CancellationToken cancellationToken)
    {
        await EnsureLegacySimulatorRuntimeRecordsAsync(cancellationToken);
        var records = await (
                from node in _database.WorkflowNodeExecutions.AsNoTracking()
                join run in _database.WorkflowExecutions.AsNoTracking()
                    on node.WorkflowRunId equals run.ExecutionId
                where (run.RuntimeStatus == WorkflowRuntimeStatus.Prepared.ToString() ||
                       run.RuntimeStatus == WorkflowRuntimeStatus.Running.ToString()) &&
                      node.Status == WorkflowNodeExecutionStatus.Ready.ToString() &&
                      node.NodeTypeId == WorkflowGraphNodeTypeIds.RobotExecuteProgram
                select node)
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return (await CreateWorkItemsAsync(records, cancellationToken))
            .Where(IsAuboProgramWorkItem)
            .ToArray();
    }

    public async Task<IReadOnlyList<WorkflowNodeExecutionWorkItem>> ListFieldNavigationDispatchableNodesAsync(
        CancellationToken cancellationToken)
    {
        await EnsureLegacySimulatorRuntimeRecordsAsync(cancellationToken);
        var records = await (
                from node in _database.WorkflowNodeExecutions.AsNoTracking()
                join run in _database.WorkflowExecutions.AsNoTracking()
                    on node.WorkflowRunId equals run.ExecutionId
                where (run.RuntimeStatus == WorkflowRuntimeStatus.Prepared.ToString() ||
                       run.RuntimeStatus == WorkflowRuntimeStatus.Running.ToString()) &&
                      node.Status == WorkflowNodeExecutionStatus.Ready.ToString() &&
                      node.NodeTypeId == WorkflowGraphNodeTypeIds.Move
                select node)
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return (await CreateWorkItemsAsync(records, cancellationToken))
            .Where(IsSimulatorWorkItem)
            .ToArray();
    }

    public async Task<IReadOnlyList<WorkflowNodeExecutionWorkItem>> ListFieldNavigationRecoverableNodesAsync(
        CancellationToken cancellationToken)
    {
        var records = await _database.WorkflowNodeExecutions
            .AsNoTracking()
            .Where(item => item.Status == WorkflowNodeExecutionStatus.Running.ToString() &&
                           item.NodeTypeId == WorkflowGraphNodeTypeIds.Move)
            .OrderBy(item => item.StartedAtUtc)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return (await CreateWorkItemsAsync(records, cancellationToken))
            .Where(IsSimulatorWorkItem)
            .ToArray();
    }

    public async Task<IReadOnlyList<WorkflowNodeExecutionWorkItem>> ListAuboProgramRecoverableNodesAsync(
        CancellationToken cancellationToken)
    {
        var records = await _database.WorkflowNodeExecutions
            .AsNoTracking()
            .Where(item => item.Status == WorkflowNodeExecutionStatus.Running.ToString() &&
                           item.NodeTypeId == WorkflowGraphNodeTypeIds.RobotExecuteProgram)
            .OrderBy(item => item.StartedAtUtc)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return (await CreateWorkItemsAsync(records, cancellationToken))
            .Where(IsAuboProgramWorkItem)
            .ToArray();
    }

    public async Task<WorkflowNodeExecutionWorkItem> ClaimNodeExecutionAsync(
        Guid nodeExecutionId,
        CancellationToken cancellationToken)
    {
        var node = await FindNodeExecutionAsync(nodeExecutionId, cancellationToken);
        var status = ParseNodeStatus(node.Status);
        if (status == WorkflowNodeExecutionStatus.Running)
        {
            return await GetWorkItemAsync(node.Id, cancellationToken);
        }

        if (status != WorkflowNodeExecutionStatus.Ready)
        {
            throw new InvalidOperationException("Only a ready node execution can be claimed.");
        }

        var nodeSnapshot = ToNodeExecutionSnapshot(node);
        if (!IsDeviceWorkItem(new WorkflowNodeExecutionWorkItem { NodeExecution = nodeSnapshot }))
        {
            throw new InvalidOperationException("The node execution is not a supported device operation.");
        }

        var run = await FindExecutionAsync(node.WorkflowRunId, cancellationToken);
        var runStatus = WorkflowPersistence.ToExecutionSnapshot(run).RuntimeStatus;
        if (runStatus is not (WorkflowRuntimeStatus.Prepared or WorkflowRuntimeStatus.Running))
        {
            throw new InvalidOperationException(
                $"A workflow run in '{runStatus}' state cannot claim a ready node.");
        }

        if (await _database.WorkflowNodeExecutions.AnyAsync(
                item => item.WorkflowRunId == node.WorkflowRunId &&
                        item.Id != node.Id &&
                        item.Status == WorkflowNodeExecutionStatus.Running.ToString(),
                cancellationToken))
        {
            throw new InvalidOperationException("The workflow run already has a running node execution.");
        }

        var step = CreateStepRequest(run, node);
        run.CurrentNodeId = node.NodeId;
        run.PendingStepJson = WorkflowPersistence.Serialize(step);
        run.RuntimeStatus = WorkflowRuntimeStatus.Prepared.ToString();
        run.TransportOperationId = null;
        run.Attempt = Math.Max(0, node.Attempt - 1);
        run.LastError = null;

        await ClaimNextStepAsync(run.ExecutionId, cancellationToken);
        return await GetWorkItemAsync(node.Id, cancellationToken);
    }

    public async Task<WorkflowExecutionSnapshot> CompleteNodeExecutionAsync(
        Guid nodeExecutionId,
        WorkflowNodeExecutionCompletionRequest completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var node = await FindNodeExecutionAsync(nodeExecutionId, cancellationToken);
        if (ParseNodeStatus(node.Status) != WorkflowNodeExecutionStatus.Running)
        {
            throw new InvalidOperationException("Only a running node execution can be completed.");
        }

        var run = await FindExecutionAsync(node.WorkflowRunId, cancellationToken);
        var step = CreateStepRequest(run, node);
        Guid compatibilityOperationId;
        if (IsNodeType(node.NodeTypeId, WorkflowGraphNodeTypeIds.Move) ||
            IsNodeType(node.NodeTypeId, WorkflowGraphNodeTypeIds.RobotExecuteProgram))
        {
            if (completion.DeviceOperationId is not { } operationId ||
                operationId == Guid.Empty)
            {
                throw new InvalidOperationException("The completion does not match the node's device operation.");
            }

            var device = await _database.WorkflowDeviceOperations
                .SingleOrDefaultAsync(
                    item => item.OperationId == operationId && item.NodeExecutionId == node.Id,
                    cancellationToken) ??
                throw new InvalidOperationException("The completion does not match the node's device operation.");
            compatibilityOperationId = device.OperationId;
        }
        else if (IsTimedWait(node.NodeTypeId))
        {
            if (completion.DeviceOperationId is { } operationId && operationId != Guid.Empty)
            {
                throw new InvalidOperationException("A Timed Wait cannot be completed with a device operation.");
            }

            compatibilityOperationId = WorkflowPersistence.CreateStableOperationId(
                run.ExecutionId,
                node.NodeId,
                node.Attempt);
        }
        else
        {
            throw new InvalidOperationException("The node execution is not supported by a device worker.");
        }

        var preservePause = string.Equals(
            run.RuntimeStatus,
            WorkflowRuntimeStatus.Paused.ToString(),
            StringComparison.Ordinal);
        run.CurrentNodeId = node.NodeId;
        run.PendingStepJson = WorkflowPersistence.Serialize(step);
        run.RuntimeStatus = (preservePause
            ? WorkflowRuntimeStatus.Paused
            : WorkflowRuntimeStatus.Running).ToString();
        run.TransportOperationId = compatibilityOperationId;
        run.Attempt = node.Attempt;
        run.LastError = null;

        return await CompleteClaimedStepAsync(run.ExecutionId, new WorkflowStepCompletionRequest
        {
            TransportOperationId = compatibilityOperationId,
            Outcome = completion.Outcome,
            Error = completion.Error,
            Outputs = completion.Outputs
        }, cancellationToken);
    }

    public async Task RecordDeviceOperationProgressAsync(
        Guid nodeExecutionId,
        Guid deviceOperationId,
        WorkflowDeviceOperationStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        if (status is not WorkflowDeviceOperationStatus.Accepted and
            not WorkflowDeviceOperationStatus.Running)
        {
            throw new ArgumentException("Only Accepted or Running progress can be recorded.", nameof(status));
        }

        var node = await FindNodeExecutionAsync(nodeExecutionId, cancellationToken);
        if (ParseNodeStatus(node.Status) != WorkflowNodeExecutionStatus.Running)
        {
            throw new InvalidOperationException("Device progress requires a running node execution.");
        }

        var operation = await _database.WorkflowDeviceOperations.SingleOrDefaultAsync(
            item => item.OperationId == deviceOperationId && item.NodeExecutionId == nodeExecutionId,
            cancellationToken) ?? throw new InvalidOperationException("The device operation does not belong to the node execution.");
        var current = Enum.TryParse<WorkflowDeviceOperationStatus>(operation.Status, true, out var parsed)
            ? parsed
            : WorkflowDeviceOperationStatus.Unknown;
        if (current == status ||
            (current == WorkflowDeviceOperationStatus.Running && status == WorkflowDeviceOperationStatus.Accepted))
        {
            return;
        }

        if (current is not WorkflowDeviceOperationStatus.Prepared and
            not WorkflowDeviceOperationStatus.Accepted)
        {
            throw new InvalidOperationException($"Device operation progress cannot advance from '{current}'.");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        operation.Status = status.ToString();
        operation.LastError = string.IsNullOrWhiteSpace(error) ? null : error.Trim();
        operation.UpdatedAtUtc = now;
        var run = await FindExecutionAsync(node.WorkflowRunId, cancellationToken);
        AddRuntimeAudit(run, "WorkflowDeviceOperationUpdated", status.ToString(), operation.LastError,
            new Dictionary<string, string?>
            {
                ["nodeExecutionId"] = node.Id.ToString(),
                ["nodeId"] = node.NodeId.ToString(),
                ["deviceOperationId"] = operation.OperationId.ToString(),
                ["attempt"] = node.Attempt.ToString()
            });
        await _database.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureLegacySimulatorRuntimeRecordsAsync(CancellationToken cancellationToken)
    {
        var runs = await _database.WorkflowExecutions
            .Where(item => item.RuntimeStatus == WorkflowRuntimeStatus.Prepared.ToString() ||
                           item.RuntimeStatus == WorkflowRuntimeStatus.Running.ToString())
            .OrderBy(item => item.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
        foreach (var run in runs)
        {
            var hasActiveNode = await _database.WorkflowNodeExecutions.AnyAsync(
                item => item.WorkflowRunId == run.ExecutionId &&
                        (item.Status == WorkflowNodeExecutionStatus.Ready.ToString() ||
                         item.Status == WorkflowNodeExecutionStatus.Running.ToString()),
                cancellationToken);
            if (hasActiveNode)
            {
                continue;
            }

            var step = WorkflowPersistence.DeserializePendingStep(run.PendingStepJson) ??
                       WorkflowPersistence.DeserializeResult(run.ResultJson).NextStepRequest;
            if (!IsSimulatorStep(step)) continue;
            var node = await GetOrCreateNodeExecutionRecordAsync(
                run,
                step!,
                Math.Max(1, run.Attempt),
                run.UpdatedAtUtc ?? run.CreatedAtUtc,
                cancellationToken);
            await BackfillRunningNodeOperationAsync(run, node, cancellationToken);
        }

        if (_database.ChangeTracker.HasChanges())
        {
            await _database.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task BackfillRunningNodeOperationAsync(
        WorkflowExecutionRecord run,
        WorkflowNodeExecutionRecord node,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(run.RuntimeStatus, WorkflowRuntimeStatus.Running.ToString(), StringComparison.OrdinalIgnoreCase) ||
            run.TransportOperationId is not { } operationId ||
            ParseNodeStatus(node.Status) is not (WorkflowNodeExecutionStatus.Ready or WorkflowNodeExecutionStatus.Running) ||
            !IsSimulatorWorkItem(new WorkflowNodeExecutionWorkItem
            {
                NodeExecution = ToNodeExecutionSnapshot(node)
            }))
        {
            return;
        }

        if (ParseNodeStatus(node.Status) == WorkflowNodeExecutionStatus.Running)
        {
            if (IsTimedWait(node.NodeTypeId)) return;
            if (await _database.WorkflowDeviceOperations.AnyAsync(
                    item => item.NodeExecutionId == node.Id,
                    cancellationToken))
            {
                return;
            }
        }

        await EnsureClaimRuntimeRecordsAsync(
            run,
            CreateStepRequest(run, node),
            operationId,
            Math.Max(1, run.Attempt),
            run.UpdatedAtUtc ?? run.CreatedAtUtc,
            cancellationToken);
    }

    private async Task<IReadOnlyList<WorkflowNodeExecutionWorkItem>> CreateWorkItemsAsync(
        IReadOnlyList<WorkflowNodeExecutionRecord> nodes,
        CancellationToken cancellationToken)
    {
        if (nodes.Count == 0) return [];
        var nodeIds = nodes.Select(item => item.Id).ToArray();
        var operations = await _database.WorkflowDeviceOperations
            .AsNoTracking()
            .Where(item => nodeIds.Contains(item.NodeExecutionId))
            .OrderBy(item => item.RequestedAtUtc)
            .ToListAsync(cancellationToken);
        var operationByNode = operations
            .GroupBy(item => item.NodeExecutionId)
            .ToDictionary(group => group.Key, group => group.Last());
        return nodes.Select(node => new WorkflowNodeExecutionWorkItem
        {
            NodeExecution = ToNodeExecutionSnapshot(node),
            DeviceOperation = operationByNode.TryGetValue(node.Id, out var operation)
                ? ToDeviceOperationSnapshot(operation)
                : null
        }).ToArray();
    }

    private async Task<WorkflowNodeExecutionWorkItem> GetWorkItemAsync(
        Guid nodeExecutionId,
        CancellationToken cancellationToken)
    {
        var node = await _database.WorkflowNodeExecutions
            .AsNoTracking()
            .SingleAsync(item => item.Id == nodeExecutionId, cancellationToken);
        return (await CreateWorkItemsAsync([node], cancellationToken))[0];
    }

    private async Task<WorkflowNodeExecutionRecord> FindNodeExecutionAsync(
        Guid nodeExecutionId,
        CancellationToken cancellationToken)
    {
        if (nodeExecutionId == Guid.Empty)
        {
            throw new ArgumentException("A node execution id is required.", nameof(nodeExecutionId));
        }

        return await _database.WorkflowNodeExecutions.SingleOrDefaultAsync(
                   item => item.Id == nodeExecutionId,
                   cancellationToken) ??
               throw new KeyNotFoundException($"Workflow node execution '{nodeExecutionId}' was not found.");
    }

    private static WorkflowNextStepRequest CreateStepRequest(
        WorkflowExecutionRecord run,
        WorkflowNodeExecutionRecord node)
    {
        var inputs = WorkflowPersistence.DeserializeDetails(node.InputJson);
        var parameters = new Dictionary<string, string?>(inputs, StringComparer.OrdinalIgnoreCase);
        parameters.Remove(WorkflowNodeConfigurationKeys.TargetStation);
        inputs.TryGetValue(WorkflowNodeConfigurationKeys.TargetStation, out var targetStation);
        return new WorkflowNextStepRequest
        {
            StepRequestId = node.StepRequestId,
            ExecutionId = run.ExecutionId,
            WorkflowId = run.WorkflowId,
            Version = run.Version,
            NodeId = node.NodeId,
            NodeType = IsNodeType(node.NodeTypeId, WorkflowGraphNodeTypeIds.Move)
                ? WorkflowNodeType.Move
                : IsNodeType(node.NodeTypeId, WorkflowGraphNodeTypeIds.RobotExecuteProgram)
                    ? WorkflowNodeType.RobotProgram
                    : WorkflowNodeType.Wait,
            NodeTypeId = node.NodeTypeId,
            NodeName = node.NodeName,
            TargetStation = targetStation,
            DryRun = false,
            Parameters = parameters
        };
    }

    private static bool IsSimulatorStep(WorkflowNextStepRequest? step) =>
        step is { NodeType: WorkflowNodeType.Move } && !string.IsNullOrWhiteSpace(step.TargetStation) ||
        step is { NodeType: WorkflowNodeType.Wait } && step.Parameters.Keys.Any(key =>
            StringComparer.OrdinalIgnoreCase.Equals(key, WorkflowRuntimeParameterNames.WaitDurationSeconds));

    private static bool IsSimulatorWorkItem(WorkflowNodeExecutionWorkItem workItem)
    {
        var node = workItem.NodeExecution;
        return IsNodeType(node.NodeTypeId, WorkflowGraphNodeTypeIds.Move)
            ? node.Inputs.TryGetValue(WorkflowNodeConfigurationKeys.TargetStation, out var target) &&
              !string.IsNullOrWhiteSpace(target)
            : IsTimedWait(node.NodeTypeId) && node.Inputs.Keys.Any(key =>
                StringComparer.OrdinalIgnoreCase.Equals(key, WorkflowRuntimeParameterNames.WaitDurationSeconds));
    }

    private static bool IsAuboProgramWorkItem(WorkflowNodeExecutionWorkItem workItem)
    {
        var node = workItem.NodeExecution;
        // Include malformed/missing-program nodes so the worker can claim them
        // and record a durable Failed outcome instead of leaving a workflow
        // permanently stuck in Ready. Publication validation still rejects the
        // missing required field before normal execution.
        return IsNodeType(node.NodeTypeId, WorkflowGraphNodeTypeIds.RobotExecuteProgram);
    }

    private static bool IsDeviceWorkItem(WorkflowNodeExecutionWorkItem workItem) =>
        IsSimulatorWorkItem(workItem) || IsAuboProgramWorkItem(workItem);

    private static bool IsTimedWait(string nodeTypeId) =>
        IsNodeType(nodeTypeId, WorkflowGraphNodeTypeIds.TimedWait) ||
        IsNodeType(nodeTypeId, WorkflowGraphNodeTypeIds.Wait);

    private static bool IsNodeType(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static WorkflowNodeExecutionStatus ParseNodeStatus(string value) =>
        Enum.TryParse<WorkflowNodeExecutionStatus>(value, true, out var status)
            ? status
            : WorkflowNodeExecutionStatus.Unknown;
}
