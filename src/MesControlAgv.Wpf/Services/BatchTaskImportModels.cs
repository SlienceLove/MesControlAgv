namespace MesControlAgv.Wpf.Services;

/// <summary>
/// 从 CSV 或 Excel 文件导入的任务行。
/// </summary>
public sealed record BatchTaskImportItem(
    int SourceRowNumber,
    string TaskId,
    string SourceStation,
    string TargetStation,
    string Description,
    int Priority,
    DateTime? PlannedTime);

public sealed record BatchTaskImportIssue(int SourceRowNumber, string Message);

public sealed class BatchTaskImportResult
{
    public BatchTaskImportResult(
        IReadOnlyList<BatchTaskImportItem> tasks,
        IReadOnlyList<BatchTaskImportIssue> issues)
    {
        Tasks = tasks;
        Issues = issues;
    }

    public IReadOnlyList<BatchTaskImportItem> Tasks { get; }
    public IReadOnlyList<BatchTaskImportIssue> Issues { get; }
    public bool HasErrors => Issues.Count > 0;
}

public static class BatchTaskImportSorter
{
    /// <summary>
    /// 先按优先级降序，再按计划时间，最后按来源行号排序。
    /// 相同优先级下没有计划时间的任务排在有计划时间的任务之后。
    /// </summary>
    public static IReadOnlyList<BatchTaskImportItem> Sort(IEnumerable<BatchTaskImportItem> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        return tasks
            .OrderByDescending(task => task.Priority)
            .ThenBy(task => task.PlannedTime ?? DateTime.MaxValue)
            .ThenBy(task => task.SourceRowNumber)
            .ToArray();
    }
}
