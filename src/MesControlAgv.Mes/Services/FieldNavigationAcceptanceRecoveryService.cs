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
        try { records = await repository.ListInFlightAsync(cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogWarning(ex, "Unable to read field-navigation acceptances for reconciliation."); return; }

        foreach (var acceptance in records)
        {
            try
            {
                var task = await gateway.GetTaskAsync(acceptance.Id, cancellationToken);
                if (task is null) continue;
                var next = ToStatus(task.State);
                if (acceptance.DeviceTaskId == task.DeviceTaskId
                    && StringComparer.Ordinal.Equals(acceptance.Status, next)
                    && StringComparer.Ordinal.Equals(acceptance.LastError, task.LastError)) continue;
                acceptance.DeviceTaskId = task.DeviceTaskId ?? acceptance.DeviceTaskId;
                acceptance.Status = next;
                acceptance.LastError = task.LastError;
                await repository.SaveWithAuditAsync(acceptance, "AdapterStateReconciled", new
                {
                    source = "adapter-task-poll",
                    task.DeviceTaskId,
                    task.State,
                    task.LastError
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogWarning(ex, "Unable to reconcile field-navigation acceptance {AcceptanceId}.", acceptance.Id); }
        }
    }

    private static string ToStatus(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "accepted" => FieldNavigationAcceptanceStatuses.Accepted,
        "moving" => FieldNavigationAcceptanceStatuses.Moving,
        "arrived" or "completed" => FieldNavigationAcceptanceStatuses.Arrived,
        "cancelled" => FieldNavigationAcceptanceStatuses.Cancelled,
        "failed" => FieldNavigationAcceptanceStatuses.Failed,
        _ => FieldNavigationAcceptanceStatuses.Unknown
    };
}
