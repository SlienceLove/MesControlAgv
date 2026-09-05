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

    Task<IReadOnlyList<ExperimentResourceAvailability>> ListResourceAvailabilityAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExperimentSchedulingAuditEntry>> ListAuditsAsync(
        Guid? planId,
        Guid? experimentJobId,
        Guid? scheduleEntryId,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>
/// G5-B plan, job, and manual scheduling commands. Runtime lease acquisition
/// and workflow-run admission remain outside this boundary.
/// </summary>
public interface IExperimentSchedulingCommandService
{
    Task<ExperimentPlan> CreatePlanDraftAsync(
        SaveExperimentPlanDraftRequest request,
        CancellationToken cancellationToken);

    Task<ExperimentPlan> UpdatePlanDraftAsync(
        Guid planId,
        int version,
        SaveExperimentPlanDraftRequest request,
        CancellationToken cancellationToken);

    Task<ExperimentPlan> ValidatePlanAsync(
        Guid planId,
        int version,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken);

    Task<ExperimentPlan> PublishPlanAsync(
        Guid planId,
        int version,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken);

    Task<ExperimentPlan> CreateNextPlanDraftAsync(
        Guid planId,
        int sourceVersion,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken);

    Task<ExperimentJob> CreateJobAsync(
        CreateExperimentJobRequest request,
        CancellationToken cancellationToken);

    Task<ScheduleEntry> ScheduleJobAsync(
        Guid experimentJobId,
        ScheduleExperimentJobRequest request,
        CancellationToken cancellationToken);

    Task<ExperimentJob> UnscheduleJobAsync(
        Guid experimentJobId,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken);

    Task<ExperimentJob> CancelJobAsync(
        Guid experimentJobId,
        ExperimentSchedulingActionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// G5-C boundary for turning an already scheduled experiment job into a pinned
/// workflow run with atomic runtime leases. It never contacts a device adapter.
/// </summary>
public interface IExperimentRuntimeAdmissionService
{
    Task<ExperimentJobAdmissionResult> AdmitJobAsync(
        Guid experimentJobId,
        AdmitExperimentJobRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Device-free persistence boundary for a composed experiment run. It only
/// materializes the pinned plan/step snapshot; child workflow admission and
/// device operations belong to the later coordinator.
/// </summary>
public interface IExperimentCompositeRuntimeService
{
    Task<ExperimentRun> PrepareAsync(
        PrepareExperimentRunRequest request,
        CancellationToken cancellationToken);

    Task<ExperimentRun?> GetAsync(
        Guid experimentRunId,
        CancellationToken cancellationToken);

    Task<ExperimentRun?> GetForJobAsync(
        Guid experimentJobId,
        CancellationToken cancellationToken);

    Task<ExperimentRun> ReconcileChildAsync(
        Guid experimentRunId,
        ReconcileExperimentChildRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Advances simulator-only composite runs from durable child workflow
    /// evidence. The worker must be explicitly enabled by the active profile.
    /// </summary>
    Task<ExperimentCompositeRuntimeProcessSummary> ProcessPendingAsync(
        CancellationToken cancellationToken);
}
