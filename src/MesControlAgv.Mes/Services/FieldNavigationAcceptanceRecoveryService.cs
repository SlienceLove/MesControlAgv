using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Reconciles supervised field-navigation acceptances from the Adapter's
/// durable task state. The Adapter remains the source of AGV truth; MES only
/// mirrors the state and never issues a movement command from this worker.
/// </summary>
public sealed class FieldNavigationAcceptanceRecoveryService(
    IServiceScopeFactory scopeFactory,
    ProfileConfiguration profile,
    ILogger<FieldNavigationAcceptanceRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!profile.Features.EnableFieldNavigationAcceptance) return;
        var interval = profile.Timeouts?.TaskPollingInterval ?? TimeSpan.FromSeconds(2);
        if (interval <= TimeSpan.Zero) interval = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            await ReconcileOnceAsync(stoppingToken);
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<FieldNavigationAcceptanceRepository>();
        var gateway = scope.ServiceProvider.GetRequiredService<IAgvGateway>();
        List<Entities.FieldNavigationAcceptance> records;
        try
        {
            records = await repository.ListInFlightAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to read field-navigation acceptances for reconciliation.");
            return;
        }

        foreach (var acceptance in records)
        {
            try
            {
                var task = await gateway.GetTaskAsync(acceptance.Id, cancellationToken);
                if (task is null)
                {
                    await RecordTransientObservationAsync(
                        repository,
                        acceptance,
                        "adapter_task_temporarily_unavailable",
                        "AdapterTaskTemporarilyUnavailable",
                        cancellationToken);
                    continue;
                }

                var next = ToStatus(task.State, acceptance.Status);
                var lastError = NormalizeTransientError(task.State, task.LastError);
                if (acceptance.DeviceTaskId == task.DeviceTaskId
                    && StringComparer.Ordinal.Equals(acceptance.Status, next)
                    && StringComparer.Ordinal.Equals(acceptance.LastError, lastError)) continue;
                acceptance.DeviceTaskId = task.DeviceTaskId ?? acceptance.DeviceTaskId;
                acceptance.Status = next;
                acceptance.LastError = lastError;
                await repository.SaveWithAuditAsync(acceptance, "AdapterStateReconciled", new
                {
                    source = "adapter-task-poll",
                    task.DeviceTaskId,
                    task.State,
                    lastError
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to reconcile field-navigation acceptance {AcceptanceId}.", acceptance.Id);
                try
                {
                    await RecordTransientObservationAsync(
                        repository,
                        acceptance,
                        $"adapter_task_status_read_failed: {ex.Message}",
                        "AdapterTaskReadFailed",
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception persistenceException)
                {
                    logger.LogWarning(
                        persistenceException,
                        "Unable to persist the transient reconciliation warning for acceptance {AcceptanceId}.",
                        acceptance.Id);
                }
            }
        }
    }

    private static async Task RecordTransientObservationAsync(
        FieldNavigationAcceptanceRepository repository,
        Entities.FieldNavigationAcceptance acceptance,
        string warning,
        string eventType,
        CancellationToken cancellationToken)
    {
        if (StringComparer.Ordinal.Equals(acceptance.LastError, warning)) return;
        acceptance.LastError = warning;
        await repository.SaveWithAuditAsync(
            acceptance,
            eventType,
            new { warning },
            cancellationToken);
    }

    private static string? NormalizeTransientError(string? state, string? error)
    {
        var normalized = state?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(error)) return error;
        return normalized switch
        {
            "paused" => "agv_task_paused_waiting_for_recovery",
            "unknown" => "agv_task_status_temporarily_unknown",
            _ => null
        };
    }

    private static string ToStatus(string? state, string currentStatus) => state?.Trim().ToLowerInvariant() switch
    {
        "accepted" => FieldNavigationAcceptanceStatuses.Accepted,
        "moving" => FieldNavigationAcceptanceStatuses.Moving,
        "paused" => FieldNavigationAcceptanceStatuses.Moving,
        "arrived" or "completed" => FieldNavigationAcceptanceStatuses.Arrived,
        "cancelled" => FieldNavigationAcceptanceStatuses.Cancelled,
        "failed" => FieldNavigationAcceptanceStatuses.Failed,
        // Once motion was confirmed, one missing/unknown task observation is
        // not evidence that the write outcome became Unknown. Keep the
        // operation in flight and reconcile again; only the initial dispatch
        // boundary may classify an unconfirmed write as Unknown.
        "unknown" when currentStatus is FieldNavigationAcceptanceStatuses.Accepted or
            FieldNavigationAcceptanceStatuses.Moving => currentStatus,
        _ => FieldNavigationAcceptanceStatuses.Unknown
    };
}
