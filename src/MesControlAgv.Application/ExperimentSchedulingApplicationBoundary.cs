using MesControlAgv.Contracts.Experiments;

namespace MesControlAgv.Application;

/// <summary>
/// Read-only G5-A boundary. Scheduling commands and runtime lease acquisition
/// are deliberately introduced by later slices.
/// </summary>
public interface IExperimentSchedulingQueryService
{
    Task<IReadOnlyList<ExperimentPlan>> ListPlansAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ExperimentPlan>> ListPlanVersionsAsync(
        Guid planId,
        CancellationToken cancellationToken);

    Task<ExperimentPlan?> GetPlanAsync(
        Guid planId,
        int version,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExperimentJob>> ListJobsAsync(
        ExperimentJobStatus? status,
        CancellationToken cancellationToken);

    Task<ExperimentJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken);

    Task<ExperimentScheduleSnapshot> GetScheduleAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken);
}
