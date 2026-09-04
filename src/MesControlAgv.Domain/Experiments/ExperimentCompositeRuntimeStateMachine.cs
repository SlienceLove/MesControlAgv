using System.Security.Cryptography;
using System.Text;
using MesControlAgv.Contracts.Experiments;
using MesControlAgv.Contracts.Workflows;

namespace MesControlAgv.Domain.Experiments;

public sealed class InvalidExperimentRunTransitionException(
    ExperimentRunStatus runStatus,
    ExperimentStepRunStatus? stepStatus,
    string message) : InvalidOperationException(message)
{
    public ExperimentRunStatus RunStatus { get; } = runStatus;
    public ExperimentStepRunStatus? StepStatus { get; } = stepStatus;
}

/// <summary>
/// Pure, device-free state machine for a composed experiment plan. Persistence
/// and child workflow execution remain outside this class so a restart can
/// replay the same transitions without issuing a device command.
/// </summary>
public static class ExperimentCompositeRuntimeStateMachine
{
    public static ExperimentRun Prepare(
        Guid experimentRunId,
        Guid experimentJobId,
        Guid admissionRequestId,
        ExperimentPlan plan,
        DateTimeOffset now)
    {
        if (experimentRunId == Guid.Empty) throw new ArgumentException("An experiment run id is required.", nameof(experimentRunId));
        if (experimentJobId == Guid.Empty) throw new ArgumentException("An experiment job id is required.", nameof(experimentJobId));
        if (admissionRequestId == Guid.Empty) throw new ArgumentException("An admission request id is required.", nameof(admissionRequestId));
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.PlanId == Guid.Empty) throw new ArgumentException("The plan id is required.", nameof(plan));
        if (plan.Version <= 0) throw new ArgumentException("The plan version must be positive.", nameof(plan));

        var source = plan.WorkflowSteps.Count > 0
            ? plan.WorkflowSteps
            : LegacyStep(plan);
        var ordered = source.OrderBy(step => step.Order).ToArray();
        if (ordered.Length == 0)
            throw new ArgumentException("A composite experiment run requires at least one workflow step.", nameof(plan));

        var stepIds = new HashSet<Guid>();
        var steps = new List<ExperimentStepRun>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var step = ordered[index];
            if (step.StepId == Guid.Empty || !stepIds.Add(step.StepId))
                throw new ArgumentException("Workflow step ids must be non-empty and unique.", nameof(plan));
            if (step.Order != index + 1)
                throw new ArgumentException("Workflow step order must be contiguous and start at one.", nameof(plan));
            if (step.WorkflowId == Guid.Empty || step.WorkflowVersion <= 0)
                throw new ArgumentException("Each workflow step must reference a positive immutable workflow version.", nameof(plan));

            steps.Add(new ExperimentStepRun
            {
                StepRunId = CreateStableStepRunId(experimentRunId, step.StepId),
                ExperimentRunId = experimentRunId,
                StepId = step.StepId,
                Order = step.Order,
                WorkflowId = step.WorkflowId,
                WorkflowVersion = step.WorkflowVersion,
                Name = string.IsNullOrWhiteSpace(step.Name) ? $"步骤 {step.Order}" : step.Name.Trim(),
                Status = ExperimentStepRunStatus.Pending,
                Attempt = 1
            });
        }

        return new ExperimentRun
        {
            ExperimentRunId = experimentRunId,
            ExperimentJobId = experimentJobId,
            PlanId = plan.PlanId,
            PlanVersion = plan.Version,
            AdmissionRequestId = admissionRequestId,
            Status = ExperimentRunStatus.Prepared,
            CurrentStepOrder = steps[0].Order,
            CurrentStepRunId = null,
            Steps = steps,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static ExperimentRun Start(ExperimentRun run, DateTimeOffset now)
    {
        EnsureRun(run);
        if (run.Status != ExperimentRunStatus.Prepared)
            throw Invalid(run, null, "只有 Prepared 的复合运行才能启动。");
        var first = RequireCurrent(run);
        if (first.Status != ExperimentStepRunStatus.Pending)
            throw Invalid(run, first, "复合运行的首步骤必须处于 Pending。");
        return WithCurrent(run, first with { Status = ExperimentStepRunStatus.Ready }, ExperimentRunStatus.Running, now);
    }

    public static ExperimentRun StartCurrentStep(ExperimentRun run, Guid workflowRunId, DateTimeOffset now)
    {
        EnsureRun(run);
        if (workflowRunId == Guid.Empty) throw new ArgumentException("A child workflow run id is required.", nameof(workflowRunId));
        var current = RequireCurrent(run);
        if (run.Status != ExperimentRunStatus.Running || current.Status != ExperimentStepRunStatus.Ready)
            throw Invalid(run, current, "只有 Running 复合运行的 Ready 步骤才能开始。");
        if (current.WorkflowRunId is not null)
            throw Invalid(run, current, "当前步骤已经绑定 child workflow run，禁止重复启动。");

        return WithCurrent(
            run,
            current with
            {
                Status = ExperimentStepRunStatus.Running,
                WorkflowRunId = workflowRunId,
                StartedAt = now,
                LastError = null
            },
            ExperimentRunStatus.Running,
            now);
    }

    public static ExperimentRun CompleteCurrentStep(
        ExperimentRun run,
        WorkflowStepCompletionOutcome outcome,
        string? error,
        DateTimeOffset now)
    {
        EnsureRun(run);
        var current = RequireCurrent(run);
        if (run.Status != ExperimentRunStatus.Running || current.Status != ExperimentStepRunStatus.Running)
            throw Invalid(run, current, "只有 Running 复合运行的 Running 步骤才能完成。");
        return ApplyOutcome(run, current, outcome, error, now);
    }

    public static ExperimentRun Pause(ExperimentRun run, DateTimeOffset now)
    {
        EnsureRun(run);
        var current = RequireCurrent(run);
        if (run.Status != ExperimentRunStatus.Running || current.Status == ExperimentStepRunStatus.Running)
            throw Invalid(run, current, "复合运行正在执行设备步骤时不能在步骤边界外暂停。");
        return run with { Status = ExperimentRunStatus.Paused, UpdatedAt = now };
    }

    public static ExperimentRun Resume(ExperimentRun run, DateTimeOffset now)
    {
        EnsureRun(run);
        if (run.Status != ExperimentRunStatus.Paused)
            throw Invalid(run, null, "只有 Paused 的复合运行才能恢复。");
        return run with { Status = ExperimentRunStatus.Running, UpdatedAt = now };
    }

    public static ExperimentRun Cancel(ExperimentRun run, DateTimeOffset now, string? reason = null)
    {
        EnsureRun(run);
        var current = run.CurrentStepRunId is null ? null : RequireCurrent(run);
        if (run.IsTerminal)
            throw Invalid(run, current, "终态复合运行不能重复取消。");
        if (current?.Status is ExperimentStepRunStatus.Running or ExperimentStepRunStatus.Unknown)
            throw Invalid(run, current, "设备步骤运行中或结果未知时不能静默取消，必须先完成设备核销。");

        var steps = run.Steps
            .Select(step => step.Status is ExperimentStepRunStatus.Pending or ExperimentStepRunStatus.Ready
                ? step with { Status = ExperimentStepRunStatus.Cancelled, CompletedAt = now, LastError = reason }
                : step)
            .ToArray();
        return run with
        {
            Status = ExperimentRunStatus.Cancelled,
            Steps = steps,
            LastError = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            UpdatedAt = now
        };
    }

    public static ExperimentRun ResolveUnknown(
        ExperimentRun run,
        WorkflowStepCompletionOutcome outcome,
        string reason,
        DateTimeOffset now)
    {
        EnsureRun(run);
        var current = RequireCurrent(run);
        if (run.Status != ExperimentRunStatus.Unknown || current.Status != ExperimentStepRunStatus.Unknown)
            throw Invalid(run, current, "只有 Unknown 复合运行的 Unknown 步骤才能人工核销。");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("An unknown-result resolution reason is required.", nameof(reason));
        if (outcome == WorkflowStepCompletionOutcome.Unknown)
            throw Invalid(run, current, "Unknown 结果不能再次作为核销结论。");
        return ApplyOutcome(run, current, outcome, reason.Trim(), now);
    }

    private static ExperimentRun ApplyOutcome(
        ExperimentRun run,
        ExperimentStepRun current,
        WorkflowStepCompletionOutcome outcome,
        string? error,
        DateTimeOffset now)
    {
        var completed = current with
        {
            Status = outcome switch
            {
                WorkflowStepCompletionOutcome.Succeeded => ExperimentStepRunStatus.Succeeded,
                WorkflowStepCompletionOutcome.Failed => ExperimentStepRunStatus.Failed,
                WorkflowStepCompletionOutcome.Unknown => ExperimentStepRunStatus.Unknown,
                WorkflowStepCompletionOutcome.Cancelled => ExperimentStepRunStatus.Cancelled,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome))
            },
            CompletedAt = now,
            LastError = string.IsNullOrWhiteSpace(error) ? null : error.Trim()
        };
        var replaced = run.Steps.Select(step => step.StepRunId == current.StepRunId ? completed : step).ToArray();
        return outcome switch
        {
            WorkflowStepCompletionOutcome.Succeeded when current.Order < run.Steps.Count =>
                Advance(run, replaced, current.Order + 1, now),
            WorkflowStepCompletionOutcome.Succeeded => run with
            {
                Status = ExperimentRunStatus.Completed,
                Steps = replaced,
                UpdatedAt = now,
                LastError = null
            },
            WorkflowStepCompletionOutcome.Failed => run with
            {
                Status = ExperimentRunStatus.Failed,
                Steps = replaced,
                LastError = completed.LastError,
                UpdatedAt = now
            },
            WorkflowStepCompletionOutcome.Unknown => run with
            {
                Status = ExperimentRunStatus.Unknown,
                Steps = replaced,
                LastError = completed.LastError,
                UpdatedAt = now
            },
            WorkflowStepCompletionOutcome.Cancelled => run with
            {
                Status = ExperimentRunStatus.Cancelled,
                Steps = replaced,
                LastError = completed.LastError,
                UpdatedAt = now
            },
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
    }

    private static ExperimentRun Advance(
        ExperimentRun run,
        IReadOnlyList<ExperimentStepRun> steps,
        int nextOrder,
        DateTimeOffset now)
    {
        var next = steps.Single(step => step.Order == nextOrder);
        var advanced = steps
            .Select(step => step.StepRunId == next.StepRunId
                ? step with { Status = ExperimentStepRunStatus.Ready }
                : step)
            .ToArray();
        return run with
        {
            Status = ExperimentRunStatus.Running,
            Steps = advanced,
            CurrentStepOrder = nextOrder,
            CurrentStepRunId = next.StepRunId,
            LastError = null,
            UpdatedAt = now
        };
    }

    private static ExperimentRun WithCurrent(
        ExperimentRun run,
        ExperimentStepRun current,
        ExperimentRunStatus status,
        DateTimeOffset now)
    {
        var steps = run.Steps
            .Select(step => step.StepRunId == current.StepRunId ? current : step)
            .ToArray();
        return run with
        {
            Status = status,
            Steps = steps,
            CurrentStepOrder = current.Order,
            CurrentStepRunId = current.StepRunId,
            UpdatedAt = now
        };
    }

    private static ExperimentStepRun RequireCurrent(ExperimentRun run) =>
        run.CurrentStepRunId is { } id
            ? run.Steps.SingleOrDefault(step => step.StepRunId == id) ??
              throw Invalid(run, null, "当前步骤引用不存在。")
            : run.Steps.SingleOrDefault(step => step.Order == run.CurrentStepOrder) ??
              throw Invalid(run, null, "当前步骤顺序不存在。");

    private static void EnsureRun(ExperimentRun run) =>
        ArgumentNullException.ThrowIfNull(run);

    private static InvalidExperimentRunTransitionException Invalid(
        ExperimentRun run,
        ExperimentStepRun? step,
        string message) => new(run.Status, step?.Status, message);

    private static IReadOnlyList<ExperimentPlanWorkflowStep> LegacyStep(ExperimentPlan plan) =>
        plan.WorkflowId == Guid.Empty || plan.WorkflowVersion <= 0
            ? Array.Empty<ExperimentPlanWorkflowStep>()
            :
            [
                new ExperimentPlanWorkflowStep
                {
                    StepId = plan.PlanId,
                    Order = 1,
                    WorkflowId = plan.WorkflowId,
                    WorkflowVersion = plan.WorkflowVersion,
                    Name = plan.Name
                }
            ];

    private static Guid CreateStableStepRunId(Guid experimentRunId, Guid stepId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"experiment-step-run\u001f{experimentRunId:N}\u001f{stepId:N}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
