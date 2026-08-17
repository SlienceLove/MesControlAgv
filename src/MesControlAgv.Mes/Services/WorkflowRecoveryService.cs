using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Reconciles only a previously claimed Adapter operation after MES restart.
/// It never dispatches, retries, or creates an operation id, so an uncertain
/// device write remains UNKNOWN instead of being duplicated.
/// </summary>
public sealed class WorkflowRecoveryService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileAsync(stoppingToken);
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var workflows = scope.ServiceProvider.GetRequiredService<IWorkflowApplicationService>();
        var adapter = scope.ServiceProvider.GetRequiredService<IAgvGateway>();
        foreach (var execution in await workflows.ListRecoverableExecutionsAsync(cancellationToken))
        {
            if (execution.RuntimeStatus != WorkflowRuntimeStatus.Running ||
                execution.TransportOperationId is null)
            {
                continue;
            }

            WorkflowStepCompletionOutcome? outcome = null;
            string? error = null;
            try
            {
                var device = await adapter.GetTaskAsync(execution.TransportOperationId.Value, cancellationToken);
                switch (device?.State)
                {
                    case "arrived":
                    case "completed":
                        outcome = WorkflowStepCompletionOutcome.Succeeded;
                        break;
                    case "failed":
                        outcome = WorkflowStepCompletionOutcome.Failed;
                        error = device.LastError;
                        break;
                    case "cancelled":
                        outcome = WorkflowStepCompletionOutcome.Cancelled;
                        error = device.LastError;
                        break;
                    case null:
                        outcome = WorkflowStepCompletionOutcome.Unknown;
                        error = "adapter_task_not_found_after_restart";
                        break;
                }
            }
            catch (HttpRequestException exception)
            {
                outcome = WorkflowStepCompletionOutcome.Unknown;
                error = exception.Message;
            }
            catch (TimeoutException exception)
            {
                outcome = WorkflowStepCompletionOutcome.Unknown;
                error = exception.Message;
            }

            if (outcome is null)
            {
                continue;
            }

            await workflows.CompleteClaimedStepAsync(execution.ExecutionId, new WorkflowStepCompletionRequest
            {
                TransportOperationId = execution.TransportOperationId.Value,
                Outcome = outcome.Value,
                Error = error
            }, cancellationToken);
        }
    }
}
