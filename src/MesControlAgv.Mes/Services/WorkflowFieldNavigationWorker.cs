using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Entities;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Explicit opt-in bridge from a durable workflow Move node to one separately
/// created and operator-authorized field-navigation acceptance. Enabling the
/// worker never creates or authorizes a permit by itself.
/// </summary>
public sealed class WorkflowFieldNavigationWorkerOptions
{
    public bool Enabled { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
}

public sealed class WorkflowFieldNavigationDispatcher(
    IWorkflowApplicationService workflows,
    IFieldNavigationAcceptanceApplicationService acceptances,
    FieldNavigationAcceptanceRepository repository,
    ProfileConfiguration profile,
    WorkflowFieldNavigationWorkerOptions options,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        if (!CanRun()) return;

        foreach (var workItem in await workflows.ListFieldNavigationDispatchableNodesAsync(cancellationToken))
        {
            var acceptance = await repository.GetByWorkflowNodeExecutionIdAsync(
                workItem.NodeExecution.Id,
                cancellationToken);
            if (acceptance?.Status != FieldNavigationAcceptanceStatuses.Authorized) continue;

            var claimed = await workflows.ClaimNodeExecutionAsync(
                workItem.NodeExecution.Id,
                cancellationToken);
            if (claimed.DeviceOperation is not { } operation)
            {
                await CompleteWithEntityAsync(
                    claimed,
                    WorkflowStepCompletionOutcome.Failed,
                    "The workflow Move node was claimed without a durable device operation.",
                    acceptance,
                    cancellationToken);
                continue;
            }

            await repository.LinkDeviceOperationAsync(
                acceptance,
                operation.OperationId,
                cancellationToken);
            try
            {
                var dispatched = await acceptances.DispatchForWorkflowAsync(
                    acceptance.Id,
                    claimed.NodeExecution.Id,
                    operation.OperationId,
                    cancellationToken);
                await ApplyAcceptanceAsync(claimed, dispatched, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // The acceptance service may have crossed the Adapter boundary
                // before an unexpected response failure. Persist Unknown and
                // never claim or dispatch this node a second time automatically.
                await CompleteAsync(
                    claimed,
                    WorkflowStepCompletionOutcome.Unknown,
                    exception.Message,
                    ToResponse(acceptance),
                    cancellationToken);
            }
        }

        await RecoverAsync(cancellationToken);
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (!CanRun()) return;

        foreach (var workItem in await workflows.ListFieldNavigationRecoverableNodesAsync(cancellationToken))
        {
            var acceptance = await repository.GetByWorkflowNodeExecutionIdAsync(
                workItem.NodeExecution.Id,
                cancellationToken);
            if (acceptance is null)
            {
                await CompleteWithEntityAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    "A running physical Move node has no linked field-navigation acceptance.",
                    null,
                    cancellationToken);
                continue;
            }
            if (workItem.DeviceOperation is not { } operation ||
                acceptance.WorkflowDeviceOperationId != operation.OperationId)
            {
                await CompleteWithEntityAsync(
                    workItem,
                    WorkflowStepCompletionOutcome.Unknown,
                    "The field-navigation acceptance is not linked to the active workflow device operation.",
                    acceptance,
                    cancellationToken);
                continue;
            }

            await ApplyAcceptanceAsync(
                workItem,
                ToResponse(acceptance),
                cancellationToken);
        }
    }

    private bool CanRun() =>
        options.Enabled &&
        !profile.Features.UseSimulator &&
        profile.Features.EnableFieldNavigationAcceptance;

    private Task ApplyAcceptanceAsync(
        WorkflowNodeExecutionWorkItem workItem,
        FieldNavigationAcceptanceResponse acceptance,
        CancellationToken cancellationToken) => acceptance.Status switch
        {
            FieldNavigationAcceptanceStatuses.Arrived => CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Succeeded,
                null,
                acceptance,
                cancellationToken),
            FieldNavigationAcceptanceStatuses.Cancelled => CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Cancelled,
                acceptance.LastError,
                acceptance,
                cancellationToken),
            FieldNavigationAcceptanceStatuses.Failed or
            FieldNavigationAcceptanceStatuses.Rejected or
            FieldNavigationAcceptanceStatuses.Expired => CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Failed,
                acceptance.LastError ?? $"field_navigation_{acceptance.Status}",
                acceptance,
                cancellationToken),
            FieldNavigationAcceptanceStatuses.Unknown => CompleteAsync(
                workItem,
                WorkflowStepCompletionOutcome.Unknown,
                acceptance.LastError ?? "field_navigation_outcome_unknown",
                acceptance,
                cancellationToken),
            FieldNavigationAcceptanceStatuses.Dispatching or
            FieldNavigationAcceptanceStatuses.Accepted => RecordProgressAsync(
                workItem,
                WorkflowDeviceOperationStatus.Accepted,
                acceptance.LastError,
                cancellationToken),
            FieldNavigationAcceptanceStatuses.Moving => RecordProgressAsync(
                workItem,
                WorkflowDeviceOperationStatus.Running,
                acceptance.LastError,
                cancellationToken),
            _ => Task.CompletedTask
        };

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

    private Task CompleteWithEntityAsync(
        WorkflowNodeExecutionWorkItem workItem,
        WorkflowStepCompletionOutcome outcome,
        string? error,
        FieldNavigationAcceptance? acceptance,
        CancellationToken cancellationToken) =>
        CompleteAsync(
            workItem,
            outcome,
            error,
            acceptance is null ? null : ToResponse(acceptance),
            cancellationToken);

    private Task CompleteAsync(
        WorkflowNodeExecutionWorkItem workItem,
        WorkflowStepCompletionOutcome outcome,
        string? error,
        FieldNavigationAcceptanceResponse? acceptance,
        CancellationToken cancellationToken)
    {
        var outputs = new Dictionary<string, string?>
        {
            ["acceptanceId"] = acceptance?.Id.ToString(),
            ["permitId"] = acceptance?.PermitId,
            ["deviceTaskId"] = acceptance?.DeviceTaskId,
            ["stationId"] = acceptance?.TargetStationId,
            ["arrivedAtUtc"] = outcome == WorkflowStepCompletionOutcome.Succeeded
                ? _timeProvider.GetUtcNow().ToString("O")
                : null
        };
        return workflows.CompleteNodeExecutionAsync(
            workItem.NodeExecution.Id,
            new WorkflowNodeExecutionCompletionRequest
            {
                DeviceOperationId = workItem.DeviceOperation?.OperationId,
                Outcome = outcome,
                Error = error,
                Outputs = outputs
            },
            cancellationToken);
    }

    private static FieldNavigationAcceptanceResponse ToResponse(FieldNavigationAcceptance acceptance) => new(
        acceptance.Id,
        acceptance.Status,
        acceptance.AgvId,
        acceptance.SourceStationId,
        acceptance.TargetStationId,
        acceptance.MapName,
        acceptance.MapMd5,
        System.Text.Json.JsonSerializer.Deserialize<List<string>>(acceptance.PlannedPathJson) ?? [],
        acceptance.Description,
        acceptance.OperatorName,
        acceptance.SafetyObserverName,
        acceptance.PermitId,
        acceptance.AuthorizedAtUtc,
        acceptance.ExpiresAtUtc,
        acceptance.PermitConsumedAtUtc,
        acceptance.DeviceTaskId,
        acceptance.LastError,
        acceptance.CreatedAtUtc,
        acceptance.UpdatedAtUtc)
    {
        WorkflowRunId = acceptance.WorkflowRunId,
        WorkflowNodeExecutionId = acceptance.WorkflowNodeExecutionId,
        WorkflowDeviceOperationId = acceptance.WorkflowDeviceOperationId
    };
}

public sealed class WorkflowFieldNavigationWorker(
    IServiceScopeFactory scopeFactory,
    ProfileConfiguration profile,
    WorkflowFieldNavigationWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<WorkflowFieldNavigationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || profile.Features.UseSimulator ||
            !profile.Features.EnableFieldNavigationAcceptance) return;

        var interval = options.PollInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(2)
            : options.PollInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dispatcher = new WorkflowFieldNavigationDispatcher(
                    scope.ServiceProvider.GetRequiredService<IWorkflowApplicationService>(),
                    scope.ServiceProvider.GetRequiredService<IFieldNavigationAcceptanceApplicationService>(),
                    scope.ServiceProvider.GetRequiredService<FieldNavigationAcceptanceRepository>(),
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
                logger.LogError(
                    exception,
                    "Field-navigation workflow worker stopped one polling cycle without replaying a write.");
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
