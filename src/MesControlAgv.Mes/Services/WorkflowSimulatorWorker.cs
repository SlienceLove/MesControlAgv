using System.Globalization;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Explicit opt-in configuration for the local Simulator workflow worker. It is
/// deliberately disabled by default and is ignored for every non-Simulator
/// profile, so enabling this section can never start a physical AGV workflow.
/// </summary>
public sealed class WorkflowSimulatorWorkerOptions
{
    public bool Enabled { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Executes the narrow Simulator-only bridge from durable node work items to
/// the Adapter. The node and operation are persisted before dispatch, and every
/// ambiguous adapter outcome is persisted as Unknown rather than retried.
/// </summary>
public sealed class WorkflowSimulatorDispatcher(
    IWorkflowApplicationService workflows,
    IAgvGateway adapter,
    ProfileConfiguration profile,
    WorkflowSimulatorWorkerOptions options,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        if (!await CanUseSimulatorAdapterAsync(cancellationToken)) return;

        foreach (var workItem in await workflows.ListSimulatorDispatchableNodesAsync(cancellationToken))
        {
            var claimed = await workflows.ClaimNodeExecutionAsync(
                workItem.NodeExecution.Id,
                cancellationToken);
            await ExecuteClaimedNodeAsync(claimed, cancellationToken);
        }

        await RecoverRunningNodesAsync(cancellationToken);
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (!await CanUseSimulatorAdapterAsync(cancellationToken)) return;
        await RecoverRunningNodesAsync(cancellationToken);
    }

    private async Task<bool> CanUseSimulatorAdapterAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled || !profile.Features.UseSimulator ||
            adapter is not IAdapterRuntimeIdentityGateway identityGateway)
        {
            return false;
        }

        try
        {
            var identity = await identityGateway.GetRuntimeIdentityAsync(cancellationToken);
            return string.Equals(identity.Driver, "simulator", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private Task ExecuteClaimedNodeAsync(
        WorkflowNodeExecutionWorkItem workItem,
        CancellationToken cancellationToken) =>
        IsNodeType(workItem, WorkflowGraphNodeTypeIds.Move)
            ? DispatchClaimedMoveAsync(workItem, cancellationToken)
            : IsTimedWait(workItem)
                ? CompleteWaitWhenElapsedAsync(workItem, cancellationToken)
                : Task.CompletedTask;

    private async Task DispatchClaimedMoveAsync(
        WorkflowNodeExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        var node = workItem.NodeExecution;
        var operation = workItem.DeviceOperation;
        if (operation is null ||
            !node.Inputs.TryGetValue(WorkflowNodeConfigurationKeys.TargetStation, out var targetStation) ||
            string.IsNullOrWhiteSpace(targetStation))
        {
            return;
        }

        try
        {
            var response = await adapter.DispatchAsync(
                operation.OperationId,
                targetStation.Trim(),
                cancellationToken);
            await ApplyAdapterResponseAsync(workItem, response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The Adapter may have accepted the command before the response was
            // lost. Never retry a claimed operation after an ambiguous write.
            await CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Unknown,
                exception.Message,
                cancellationToken);
        }
    }

    private async Task RecoverRunningNodesAsync(CancellationToken cancellationToken)
    {
        foreach (var workItem in await workflows.ListSimulatorRecoverableNodesAsync(cancellationToken))
        {
            await ReconcileClaimedNodeAsync(workItem, cancellationToken);
        }
    }

    private async Task ReconcileClaimedNodeAsync(
        WorkflowNodeExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (IsTimedWait(workItem))
        {
            await CompleteWaitWhenElapsedAsync(workItem, cancellationToken);
            return;
        }

        if (!IsNodeType(workItem, WorkflowGraphNodeTypeIds.Move) ||
            workItem.DeviceOperation is not { } operation)
        {
            return;
        }

        try
        {
            var response = await adapter.GetTaskAsync(operation.OperationId, cancellationToken);
            if (response is null)
            {
                await CompleteAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    "adapter_task_not_found_during_simulator_reconciliation",
                    cancellationToken);
                return;
            }

            await ApplyAdapterResponseAsync(workItem, response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Unknown,
                exception.Message,
                cancellationToken);
        }
    }

    private Task CompleteWaitWhenElapsedAsync(
        WorkflowNodeExecutionWorkItem workItem,
        CancellationToken cancellationToken)
    {
        var node = workItem.NodeExecution;
        if (node.StartedAt is not { } startedAt ||
            !TryGetWaitDuration(node.Inputs, out var duration) ||
            _timeProvider.GetUtcNow() - startedAt < duration)
        {
            return Task.CompletedTask;
        }

        return CompleteAsync(
            workItem,
            WorkflowStepCompletionOutcome.Succeeded,
            null,
            cancellationToken,
            new Dictionary<string, string?>
            {
                ["completedAtUtc"] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)
            });
    }

    private static bool TryGetWaitDuration(
        IReadOnlyDictionary<string, string?> inputs,
        out TimeSpan duration)
    {
        var value = inputs.FirstOrDefault(parameter =>
            StringComparer.OrdinalIgnoreCase.Equals(
                parameter.Key,
                WorkflowRuntimeParameterNames.WaitDurationSeconds)).Value;
        if (!decimal.TryParse(
                value,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var seconds) ||
            seconds < 0 ||
            seconds > 86400)
        {
            duration = default;
            return false;
        }

        duration = TimeSpan.FromSeconds((double)seconds);
        return true;
    }

    private Task ApplyAdapterResponseAsync(
        WorkflowNodeExecutionWorkItem workItem,
        AgvTaskResponse response,
        CancellationToken cancellationToken)
    {
        var state = response.State?.Trim().ToLowerInvariant();
        return state switch
        {
            "arrived" or "completed" => CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Succeeded,
                null,
                cancellationToken,
                CreateMoveOutputs(response)),
            "failed" => CompleteAsync(workItem, WorkflowStepCompletionOutcome.Failed, response.LastError, cancellationToken),
            "cancelled" => CompleteAsync(workItem, WorkflowStepCompletionOutcome.Cancelled, response.LastError, cancellationToken),
            "unknown" => CompleteAsync(workItem, WorkflowStepCompletionOutcome.Unknown, response.LastError, cancellationToken),
            "created" or "queued" or "accepted" => RecordProgressAsync(
                workItem,
                WorkflowDeviceOperationStatus.Accepted,
                response.LastError,
                cancellationToken),
            "moving" or "running" => RecordProgressAsync(
                workItem,
                WorkflowDeviceOperationStatus.Running,
                response.LastError,
                cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private Task RecordProgressAsync(
        WorkflowNodeExecutionWorkItem workItem,
        WorkflowDeviceOperationStatus status,
        string? error,
        CancellationToken cancellationToken) =>
        workItem.DeviceOperation is not { } operation
            ? Task.CompletedTask
            : workflows.RecordDeviceOperationProgressAsync(
                workItem.NodeExecution.Id,
                operation.OperationId,
                status,
                error,
                cancellationToken);

    private Task CompleteAsync(
        WorkflowNodeExecutionWorkItem workItem,
        WorkflowStepCompletionOutcome outcome,
        string? error,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? outputs = null) =>
        workflows.CompleteNodeExecutionAsync(workItem.NodeExecution.Id, new WorkflowNodeExecutionCompletionRequest
        {
            DeviceOperationId = workItem.DeviceOperation?.OperationId,
            Outcome = outcome,
            Error = error,
            Outputs = outputs ?? new Dictionary<string, string?>()
        }, cancellationToken);

    private IReadOnlyDictionary<string, string?> CreateMoveOutputs(AgvTaskResponse response) =>
        new Dictionary<string, string?>
        {
            ["deviceId"] = response.AgvId ?? profile.Agvs?.FirstOrDefault()?.AgvId,
            ["stationId"] = response.TargetStationId,
            ["deviceTaskId"] = response.DeviceTaskId,
            ["arrivedAtUtc"] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)
        };

    private static bool IsNodeType(WorkflowNodeExecutionWorkItem workItem, string nodeTypeId) =>
        string.Equals(
            workItem.NodeExecution.NodeTypeId,
            nodeTypeId,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsTimedWait(WorkflowNodeExecutionWorkItem workItem) =>
        IsNodeType(workItem, WorkflowGraphNodeTypeIds.TimedWait) ||
        IsNodeType(workItem, WorkflowGraphNodeTypeIds.Wait);
}

/// <summary>
/// Periodically invokes the explicitly enabled Simulator dispatcher. The host
/// exits before resolving an Adapter for a physical profile.
/// </summary>
public sealed class WorkflowSimulatorWorker(
    IServiceScopeFactory scopeFactory,
    ProfileConfiguration profile,
    WorkflowSimulatorWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<WorkflowSimulatorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || !profile.Features.UseSimulator)
        {
            return;
        }

        var interval = options.PollInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : options.PollInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dispatcher = new WorkflowSimulatorDispatcher(
                    scope.ServiceProvider.GetRequiredService<IWorkflowApplicationService>(),
                    scope.ServiceProvider.GetRequiredService<IAgvGateway>(),
                    profile,
                    options,
                    timeProvider);
                await dispatcher.ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Simulator workflow worker stopped one polling cycle without retrying an ambiguous operation.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
