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
    int count)
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
    public bool IsRuntimeSelectable => Count == 1 && Job.HasWorkflowRun;
    public string StateDisplay => IsRuntimeSelectable
        ? Job.StatusDisplay
        : Count > 1
            ? "等待复合运行时"
            : "尚未生成运行";
    public string Display => $"步骤 {Position}/{Count} · {StepName} · {StateDisplay}";
    public string Hint => Count > 1
        ? "复合运行上下文尚未建立，无需输入运行 ID。"
        : IsRuntimeSelectable
            ? $"{StepName} · {WorkflowReference} · 可查看节点、设备操作和事件"
            : $"{StepName} · {WorkflowReference} · 该任务尚未准入运行。";
}
