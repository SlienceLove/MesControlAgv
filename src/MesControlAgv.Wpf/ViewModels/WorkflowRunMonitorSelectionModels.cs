using MesControlAgv.Contracts.Experiments;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// A business-facing selector item for an experiment job that may have a
/// workflow run. The run id is deliberately kept out of the primary display;
/// operators select a sample batch and plan instead of copying GUIDs.
/// </summary>
public sealed class WorkflowMonitorExperimentJobOption(ExperimentJob job, string planName)
{
    public ExperimentJob Job { get; } = job;
    public Guid JobId => Job.JobId;
    public string PlanName { get; } = string.IsNullOrWhiteSpace(planName) ? "未命名方案" : planName;
    public string SampleBatchId => string.IsNullOrWhiteSpace(Job.SampleBatchId)
        ? $"任务 {Job.JobId:N}"[..15]
        : Job.SampleBatchId;
    public string PlanDisplay => $"{PlanName} / v{Job.PlanVersion}";
    public string StatusDisplay => ExperimentUiText.JobStatus(Job.Status);
    public bool HasWorkflowRun => Job.WorkflowRunId is { } runId && runId != Guid.Empty;
    public string RunAvailabilityDisplay => HasWorkflowRun ? "已有运行记录" : "尚未生成运行";
    public string Display => $"{SampleBatchId} · {PlanDisplay} · {StatusDisplay}";
    public string WorkflowSummary => Job.WorkflowSteps.Count == 0
        ? $"流程 v{Job.WorkflowVersion}"
        : string.Join(" → ", Job.WorkflowSteps
            .OrderBy(step => step.Order)
            .Select(step => string.IsNullOrWhiteSpace(step.Name)
                ? $"流程 v{step.WorkflowVersion}"
                : step.Name));
    public string Hint => HasWorkflowRun
        ? $"{WorkflowSummary} · {RunAvailabilityDisplay}"
        : Job.WorkflowSteps.Count > 1
            ? $"{WorkflowSummary} · 复合运行上下文尚未建立"
            : $"{WorkflowSummary} · {RunAvailabilityDisplay}";
}

/// <summary>
/// One plan step shown below the experiment-job selector. Current runtime
/// admission creates one run for a single-step job; multi-step jobs remain
/// visible so the operator can understand why no step run can be loaded yet.
/// </summary>
public sealed class WorkflowMonitorStepOption(
    WorkflowMonitorExperimentJobOption job,
    ExperimentPlanWorkflowStep step,
    int position,
    int count,
    bool compositeRuntimeAvailable = false)
{
    public WorkflowMonitorExperimentJobOption Job { get; } = job;
    public ExperimentPlanWorkflowStep Step { get; } = step;
    public int Position { get; } = position;
    public int Count { get; } = count;
    public Guid StepId => Step.StepId;
    public string StepName => string.IsNullOrWhiteSpace(Step.Name)
        ? $"流程 v{Step.WorkflowVersion}"
        : Step.Name;
    public string WorkflowReference => $"模板 v{Step.WorkflowVersion}";
    public bool IsRuntimeSelectable => compositeRuntimeAvailable || (Count == 1 && Job.HasWorkflowRun);
    public string StateDisplay => IsRuntimeSelectable
        ? compositeRuntimeAvailable
            ? "复合运行已建立"
            : Job.StatusDisplay
        : Count > 1
            ? "等待复合运行时"
            : "尚未生成运行";
    public string Display => $"步骤 {Position}/{Count} · {StepName} · {StateDisplay}";
    public string Hint => Count > 1
        ? compositeRuntimeAvailable
            ? $"{StepName} · {WorkflowReference} · 可查看复合运行步骤状态；子流程启动后将显示节点和设备证据。"
            : "复合运行上下文尚未建立，无需输入运行 ID。"
        : IsRuntimeSelectable
            ? $"{StepName} · {WorkflowReference} · 可查看节点、设备操作和事件"
            : $"{StepName} · {WorkflowReference} · 该任务尚未准入运行。";
}

/// <summary>
/// Business-facing row for the outer composite run. It deliberately exposes
/// step names and child-run availability instead of requiring operators to copy
/// internal GUIDs from the database.
/// </summary>
public sealed class WorkflowMonitorCompositeStepItemViewModel(ExperimentStepRun step)
{
    public ExperimentStepRun Step { get; } = step;
    public int Order => Step.Order;
    public string Name => string.IsNullOrWhiteSpace(Step.Name)
        ? $"步骤 {Step.Order}"
        : Step.Name;
    public string WorkflowDisplay => $"流程 v{Step.WorkflowVersion}";
    public string StatusDisplay => ExperimentUiText.CompositeStepStatus(Step.Status);
    public string ChildRunDisplay => Step.WorkflowRunId is { } runId && runId != Guid.Empty
        ? "子流程已建立"
        : "等待子流程";
    public string TimeDisplay => Step.StartedAt is not { } startedAt
        ? "尚未开始"
        : Step.CompletedAt is { } completedAt
            ? $"{startedAt.ToLocalTime():MM-dd HH:mm:ss} → {completedAt.ToLocalTime():MM-dd HH:mm:ss}"
            : $"{startedAt.ToLocalTime():MM-dd HH:mm:ss} → 运行中";
    public string ErrorDisplay => Step.LastError ?? string.Empty;
}
