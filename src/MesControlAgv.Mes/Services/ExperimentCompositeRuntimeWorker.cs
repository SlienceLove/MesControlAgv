using MesControlAgv.Application;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Explicit opt-in configuration for the simulator-only composite workflow
/// coordinator. The worker remains inert unless both this switch and the
/// active profile's simulator feature are enabled.
/// </summary>
public sealed class ExperimentCompositeRuntimeWorkerOptions
{
    public bool Enabled { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Polls durable outer experiment snapshots and coordinates one child workflow
/// at a time. It never runs for a physical profile; child device writes remain
/// owned by the independent simulator/device workers.
/// </summary>
public sealed class ExperimentCompositeRuntimeWorker(
    IServiceScopeFactory scopeFactory,
    ProfileConfiguration profile,
    ExperimentCompositeRuntimeWorkerOptions options,
    ILogger<ExperimentCompositeRuntimeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || !profile.Features.UseSimulator)
        {
            return;
        }

        var interval = options.PollInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : options.PollInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var summary = await scope.ServiceProvider
                    .GetRequiredService<IExperimentCompositeRuntimeService>()
                    .ProcessPendingAsync(stoppingToken);
                if (summary.Changed > 0 || summary.Unknown > 0)
                {
                    logger.LogInformation(
                        "Simulator composite runtime pass scanned {Scanned} runs, changed {Changed}, unknown {Unknown}.",
                        summary.Scanned,
                        summary.Changed,
                        summary.Unknown);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Simulator composite runtime worker stopped one polling cycle; no automatic child retry was requested.");
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
