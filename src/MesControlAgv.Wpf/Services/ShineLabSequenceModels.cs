namespace MesControlAgv.Wpf.Services;

/// <summary>
/// ShineLab 样品任务行。字段与 res/ExportData.csv 模板一一对应，全部按字符串语义保留，
/// 避免丢失 "07" 这类前导零。展示字段（序号、选择、数据名称）由 ShineLab 自行编号，不在此建模。
/// </summary>
public sealed record ShineLabSampleTask(
    int SourceRowNumber,
    string SampleName,
    string SampleType,
    string SampleLevel,
    string ProcessMethod,
    string ClearCalibration,
    string CycleCount,
    string InjectionVolume,
    string InjectionUnit,
    string Blank,
    string ChromatographyMethod);

public sealed record ShineLabSampleTaskIssue(int SourceRowNumber, string Message);

public sealed class ShineLabSequenceImportResult
{
    public ShineLabSequenceImportResult(
        IReadOnlyList<ShineLabSampleTask> tasks,
        IReadOnlyList<ShineLabSampleTaskIssue> issues)
    {
        Tasks = tasks;
        Issues = issues;
    }

    public IReadOnlyList<ShineLabSampleTask> Tasks { get; }
    public IReadOnlyList<ShineLabSampleTaskIssue> Issues { get; }
    public bool HasIssues => Issues.Count > 0;

    /// <summary>
    /// 只有零问题且至少一条任务才允许生成 ShineLab CSV 并下发批次。
    /// </summary>
    public bool CanGenerateSequence => Issues.Count == 0 && Tasks.Count > 0;
}
