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
/// Executes the narrow Simulator-only bridge from a durable workflow claim to
/// the Adapter. The operation is claimed before dispatch, and every ambiguous
/// adapter outcome is persisted as Unknown rather than retried.
/// </summary>
public sealed class WorkflowSimulatorDispatcher(
    IWorkflowApplicationService workflows,
    IAgvGateway adapter,
    ProfileConfiguration profile,
    WorkflowSimulatorWorkerOptions options)
{
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled || !profile.Features.UseSimulator)
        {
            return;
        }

        if (adapter is not IAdapterRuntimeIdentityGateway identityGateway)
        {
            return;
        }

        AdapterRuntimeIdentityResponse identity;
        try
        {
            identity = await identityGateway.GetRuntimeIdentityAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return;
        }

        if (!string.Equals(identity.Driver, "simulator", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var execution in await workflows.ListSimulatorDispatchableExecutionsAsync(cancellationToken))
        {
            var claimed = await workflows.ClaimNextStepAsync(execution.ExecutionId, cancellationToken);
            await DispatchClaimedStepAsync(claimed, cancellationToken);
        }

        foreach (var execution in await workflows.ListRecoverableExecutionsAsync(cancellationToken))
        {
            if (execution.RuntimeStatus == WorkflowRuntimeStatus.Running &&
                execution.TransportOperationId is not null)
            {
                await ReconcileClaimedStepAsync(execution, cancellationToken);
            }
        }
    }

    private async Task DispatchClaimedStepAsync(
        WorkflowExecutionSnapshot claimed,
        CancellationToken cancellationToken)
    {
        var step = claimed.PendingStepRequest;
        var operationId = claimed.TransportOperationId;
        if (step is null || operationId is null)
        {
            return;
        }

        if (step.NodeType != WorkflowNodeType.Move || string.IsNullOrWhiteSpace(step.TargetStation))
        {
            return;
        }

        try
        {
            var response = await adapter.DispatchAsync(
                operationId.Value,
                step.TargetStation.Trim(),
                cancellationToken);
            await ApplyAdapterResponseAsync(claimed.ExecutionId, operationId.Value, response, cancellationToken);
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
                claimed.ExecutionId,
                operationId.Value,
                WorkflowStepCompletionOutcome.Unknown,
                exception.Message,
                cancellationToken);
        }
    }

    private async Task ReconcileClaimedStepAsync(
        WorkflowExecutionSnapshot execution,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await adapter.GetTaskAsync(execution.TransportOperationId!.Value, cancellationToken);
            if (response is null)
            {
                await CompleteAsync(
                    execution.ExecutionId,
                    execution.TransportOperationId.Value,
                    WorkflowStepCompletionOutcome.Unknown,
                    "adapter_task_not_found_during_simulator_reconciliation",
                    cancellationToken);
                return;
            }

            await ApplyAdapterResponseAsync(
                execution.ExecutionId,
                execution.TransportOperationId.Value,
                response,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await CompleteAsync(
                execution.ExecutionId,
                execution.TransportOperationId!.Value,
                WorkflowStepCompletionOutcome.Unknown,
                exception.Message,
                cancellationToken);
        }
    }

    private Task ApplyAdapterResponseAsync(
        Guid executionId,
        Guid operationId,
        AgvTaskResponse response,
        CancellationToken cancellationToken)
    {
        var state = response.State?.Trim().ToLowerInvariant();
        return state switch
        {
            "arrived" or "completed" => CompleteAsync(executionId, operationId, WorkflowStepCompletionOutcome.Succeeded, null, cancellationToken),
            "failed" => CompleteAsync(executionId, operationId, WorkflowStepCompletionOutcome.Failed, response.LastError, cancellationToken),
            "cancelled" => CompleteAsync(executionId, operationId, WorkflowStepCompletionOutcome.Cancelled, response.LastError, cancellationToken),
            "unknown" => CompleteAsync(executionId, operationId, WorkflowStepCompletionOutcome.Unknown, response.LastError, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private Task CompleteAsync(
        Guid executionId,
        Guid operationId,
        WorkflowStepCompletionOutcome outcome,
        string? error,
        CancellationToken cancellationToken) =>
        workflows.CompleteClaimedStepAsync(executionId, new WorkflowStepCompletionRequest
        {
            TransportOperationId = operationId,
            Outcome = outcome,
            Error = error
        }, cancellationToken);
}

/// <summary>
/// Periodically invokes the explicitly enabled Simulator dispatcher. The host
/// exits before resolving an Adapter for a physical profile.
/// </summary>
public sealed class WorkflowSimulatorWorker(
    IServiceScopeFactory scopeFactory,
    ProfileConfiguration profile,
    WorkflowSimulatorWorkerOptions options,
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
                    options);
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
