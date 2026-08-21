using MesControlAgv.Application;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Runs one Simulator-only node-record reconciliation pass after MES restart.
/// It never dispatches a ready node; the periodic worker owns new dispatches.
/// </summary>
public sealed class WorkflowRecoveryService(
    IServiceScopeFactory scopeFactory,
    ProfileConfiguration profile,
    WorkflowSimulatorWorkerOptions options,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || !profile.Features.UseSimulator) return;
        using var scope = scopeFactory.CreateScope();
        var dispatcher = new WorkflowSimulatorDispatcher(
            scope.ServiceProvider.GetRequiredService<IWorkflowApplicationService>(),
            scope.ServiceProvider.GetRequiredService<IAgvGateway>(),
            profile,
            options,
            timeProvider);
        await dispatcher.RecoverAsync(stoppingToken);
    }
}
