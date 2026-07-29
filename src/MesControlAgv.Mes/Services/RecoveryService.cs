namespace MesControlAgv.Mes.Services;

public sealed class RecoveryService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<TaskService>();
        await tasks.ReconcileIncompleteAsync(stoppingToken);
    }
}
