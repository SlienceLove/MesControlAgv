namespace MesControlAgv.Reporting;

public sealed record ReportQuery(DateTimeOffset From, DateTimeOffset To, string? Batch = null);
public sealed record ReportJob(string Id, string SampleId, string Batch, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Error);
public sealed record Measurement(string RecordId, string SampleId, string Batch, string Metric,
    decimal Value, string Unit, DateTimeOffset MeasuredAt, string Source, string FileSha256, int SourceRow);
public sealed record ImportIssue(int Row, string Message);
public sealed record ImportPreview(Measurement[] Records, ImportIssue[] Issues, string FileSha256)
{
    public bool CanAccept => Records.Length > 0 && Issues.Length == 0;
}
public sealed record ReportSnapshot(string Id, DateTimeOffset CapturedAt, ReportQuery Query,
    ReportJob[] Jobs, Measurement[] Measurements, string[] Warnings);
public sealed record ReportStatistics(int Total, int Completed, int Failed, int Cancelled,
    int Other, decimal? SuccessPercent, double? AverageSeconds, int DurationCount, int InvalidCompletedTimes);
public sealed record MeasurementStatistic(string Metric, string Unit, int Count, decimal Minimum,
    decimal Maximum, decimal Average);
public sealed record AiSummary(bool Available, string Message, string Model,
    string Text, string[] References, DateTimeOffset GeneratedAt);

public static class ReportRules
{
    public const int MaxRows = 10_000;
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public const string Scope = "按创建时间筛选 [开始,结束)，截至快照时的任务状态；成功率=完成/(完成+失败)，取消及未结束不计分母。";

    public static void ValidateQuery(ReportQuery query)
    {
        if (query.To <= query.From || query.To - query.From > TimeSpan.FromDays(366))
            throw new ArgumentException("请选择不超过 366 天的有效时间范围。");
        if (query.Batch?.Length > 128) throw new ArgumentException("批次编号过长。");
    }

    public static ReportStatistics Calculate(IEnumerable<ReportJob> source)
    {
        var rows = source.GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.First()).ToArray();
        var completed = rows.Where(x => x.Status == "Completed").ToArray();
        var failed = rows.Count(x => x.Status == "Failed");
        var cancelled = rows.Count(x => x.Status == "Cancelled");
        var durations = completed.Where(x => x.StartedAt.HasValue && x.CompletedAt >= x.StartedAt)
            .Select(x => (x.CompletedAt!.Value - x.StartedAt!.Value).TotalSeconds).ToArray();
        return new(rows.Length, completed.Length, failed, cancelled,
            rows.Length - completed.Length - failed - cancelled,
            completed.Length + failed == 0 ? null : decimal.Round(100m * completed.Length / (completed.Length + failed), 2),
            durations.Length == 0 ? null : durations.Average(), durations.Length, completed.Length - durations.Length);
    }

    public static MeasurementStatistic[] Aggregate(IEnumerable<Measurement> source) => source
        .GroupBy(x => (x.Metric, x.Unit)).Select(g => new MeasurementStatistic(g.Key.Metric,
            g.Key.Unit, g.Count(), g.Min(x => x.Value), g.Max(x => x.Value), g.Average(x => x.Value))).ToArray();

    public static string[] MeasurementWarnings(Measurement[] measurements, ReportJob[] jobs)
    {
        var warnings = new List<string>();
        foreach (var group in measurements.GroupBy(x => x.Metric).Where(g => g.Select(x => x.Unit).Distinct().Count() > 1))
            warnings.Add($"指标 {group.Key} 存在不同单位，分组统计，未自动换算。");
        var unmatched = measurements.Count(m => jobs.Count(j => j.SampleId == m.SampleId && j.Batch == m.Batch) != 1);
        if (unmatched > 0) warnings.Add($"{unmatched} 条测量记录未能唯一关联 MES 任务，保留为独立导入数据。");
        return warnings.ToArray();
    }

    public static ReportSnapshot Empty(ReportQuery query) => new(Guid.NewGuid().ToString("N"),
        DateTimeOffset.UtcNow, query, [], [], ["尚未读取 MES；仅包含已确认导入的测量记录。"]);
}
