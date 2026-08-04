namespace MesControlAgv.Wpf.Services;

/// <summary>
/// A task row imported from a CSV or Excel workbook.
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
    /// Sorts higher priorities first, then planned tasks by time, and finally by source row.
    /// Tasks without a planned time are placed after planned tasks at the same priority.
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
