using MesControlAgv.Application;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Mes.Services;

public sealed class RecoveryService(IServiceScopeFactory scopeFactory, ProfileConfiguration profile) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileAsync(static tasks => tasks.ReconcileIncompleteAsync, stoppingToken);

        var interval = profile.Timeouts?.TaskPollingInterval ?? TimeSpan.FromSeconds(2);
        if (interval <= TimeSpan.Zero) interval = TimeSpan.FromSeconds(2);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await ReconcileAsync(static tasks => tasks.ReconcileActiveAsync, stoppingToken);
        }
    }

    private async Task ReconcileAsync(
        Func<ITaskApplicationService, Func<CancellationToken, Task>> operation,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskApplicationService>();
        try
        {
            await operation(tasks)(cancellationToken);
        }
        catch (HttpRequestException)
        {
            // An unavailable Adapter leaves affected tasks unresolved; the host remains available.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A transport timeout is unresolved recovery state, not a host shutdown.
        }
    }
}
