namespace MesControlAgv.Mes.Services;

public sealed class RecoveryService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<TaskService>();
        try
        {
            await tasks.ReconcileIncompleteAsync(stoppingToken);
        }
        catch (HttpRequestException)
        {
            // An unavailable Adapter leaves affected tasks unresolved; the host remains available.
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // A transport timeout is unresolved recovery state, not a host shutdown.
        }
    }
}
