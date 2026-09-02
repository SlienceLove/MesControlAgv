namespace MesControlAgv.Mes.Services;

/// <summary>
/// A restarted MES cannot assume that an accepted/running ShineLab command
/// did or did not execute.  Such tasks are recovered as Unknown until a fresh
/// ShineLab status or operator reconciliation resolves them.
/// </summary>
public sealed class ShineLabTaskRecoveryService(
    IServiceScopeFactory scopeFactory,
    ILogger<ShineLabTaskRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ShineLabTaskRepository>();
        var count = await repository.MarkInterruptedTasksUnknownAsync(cancellationToken);
        if (count > 0)
            logger.LogWarning("Recovered {Count} interrupted ShineLab tasks as Unknown.", count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
