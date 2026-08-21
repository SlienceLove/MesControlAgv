using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Entities;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Services;

public sealed record ExperimentRuntimeRecoverySummary(
    int LinkedJobsInspected,
    int RecoveryFailures,
    int ReleasedDetachedLeases);

/// <summary>
/// Reconciles durable experiment/run/lease linkage without polling or commanding
/// any device. Non-terminal accepted runs retain their leases even after expiry.
/// </summary>
public sealed class ExperimentRuntimeRecoveryCoordinator(
    MesDbContext database,
    ExperimentRuntimeLeaseLifecycle leaseLifecycle)
{
    public async Task<ExperimentRuntimeRecoverySummary> ReconcileAsync(
        CancellationToken cancellationToken)
    {
        var jobs = await database.ExperimentJobs
            .Where(job => job.WorkflowRunId != null)
            .OrderBy(job => job.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var runIds = jobs.Select(job => job.WorkflowRunId!.Value).Distinct().ToArray();
        var runs = await database.WorkflowExecutions
            .Where(run => runIds.Contains(run.ExecutionId))
            .ToDictionaryAsync(run => run.ExecutionId, cancellationToken);

        var failures = 0;
        foreach (var job in jobs)
        {
            if (!runs.TryGetValue(job.WorkflowRunId!.Value, out var run))
            {
                failures++;
                await leaseLifecycle.MarkRecoveryFailureAsync(
                    job,
                    $"Linked workflow run '{job.WorkflowRunId}' was not found during MES recovery.",
                    cancellationToken);
                continue;
            }
            if (!CanOwnRuntimeResources(run))
            {
                failures++;
                await leaseLifecycle.MarkRecoveryFailureAsync(
                    job,
                    $"Linked workflow run '{run.ExecutionId}' was rejected, a dry run, or has invalid admission evidence.",
                    cancellationToken);
                continue;
            }

            await leaseLifecycle.SynchronizeRunStateAsync(
                run,
                "workflow-recovery",
                "Reconcile experiment state after MES restart.",
                cancellationToken);
        }

        var releasedDetachedLeases = 0;
        var activeLeases = await database.WorkflowResourceLeases
            .Where(lease => lease.ActiveResourceKey != null)
            .OrderBy(lease => lease.AcquiredAtUtc)
            .ToListAsync(cancellationToken);
        foreach (var leaseGroup in activeLeases
                     .Where(lease => lease.ActiveResourceKey != null)
                     .GroupBy(lease => lease.WorkflowRunId))
        {
            if (jobs.Any(job => job.WorkflowRunId == leaseGroup.Key)) continue;

            var run = await database.WorkflowExecutions.SingleOrDefaultAsync(
                item => item.ExecutionId == leaseGroup.Key,
                cancellationToken);
            if (run is not null &&
                CanOwnRuntimeResources(run) &&
                !IsTerminal(run.RuntimeStatus))
            {
                continue;
            }

            releasedDetachedLeases += await leaseLifecycle.ReleaseDetachedLeasesAsync(
                leaseGroup.Key,
                run is null
                    ? "The lease's workflow run was not found during MES recovery."
                    : "The lease's workflow run cannot retain runtime resources after recovery.",
                cancellationToken);
        }

        await database.SaveChangesAsync(cancellationToken);
        return new ExperimentRuntimeRecoverySummary(jobs.Count, failures, releasedDetachedLeases);
    }

    private static bool CanOwnRuntimeResources(WorkflowExecutionRecord run)
    {
        if (!string.Equals(run.Outcome, WorkflowExecutionStatus.Accepted.ToString(), StringComparison.OrdinalIgnoreCase) ||
            run.ExecutionId == Guid.Empty)
        {
            return false;
        }

        try
        {
            return !WorkflowPersistence.DeserializeRequest(run.RequestJson).DryRun;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsTerminal(string? runtimeStatus) =>
        Enum.TryParse<WorkflowRuntimeStatus>(runtimeStatus, ignoreCase: true, out var status) &&
        status is WorkflowRuntimeStatus.Completed or
            WorkflowRuntimeStatus.Failed or
            WorkflowRuntimeStatus.Cancelled or
            WorkflowRuntimeStatus.Rejected or
            WorkflowRuntimeStatus.DryRunCompleted;
}

/// <summary>Runs one device-free experiment linkage reconciliation pass at startup.</summary>
public sealed class ExperimentRuntimeRecoveryService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<ExperimentRuntimeRecoveryCoordinator>()
            .ReconcileAsync(stoppingToken);
    }
}
