using System.Globalization;
using System.Net;
using System.Text;
using ClosedXML.Excel;

namespace MesControlAgv.Reporting;

public static class ReportFiles
{
    public static void WriteExcel(string path, ReportSnapshot snapshot, AiSummary? summary)
    {
        using var book = new XLWorkbook();
        var data = book.AddWorksheet("原始记录");
        var headers = MeasurementFiles.Headers.Concat(["文件SHA256", "来源行号"]).ToArray();
        for (var c = 0; c < headers.Length; c++) data.Cell(1, c + 1).Value = headers[c];
        data.Columns(1, 3).Style.NumberFormat.Format = "@";
        for (var r = 0; r < snapshot.Measurements.Length; r++)
        {
            var m = snapshot.Measurements[r]; var row = r + 2;
            var values = MeasurementValues(m);
            for (var c = 0; c < values.Length; c++) data.Cell(row, c + 1).Value = values[c];
            // Excel has 15 significant digits; retain higher precision as exact text.
            if (m.Value.ToString("G29", CultureInfo.InvariantCulture).Count(char.IsDigit) <= 15)
                data.Cell(row, 5).Value = (double)m.Value;
            else data.Cell(row, 5).Style.NumberFormat.Format = "@";
            data.Cell(row, 9).Value = m.FileSha256;
            data.Cell(row, 10).Value = m.SourceRow;
        }
        Style(data, snapshot.Measurements.Length + 1, 10);

        var jobs = book.AddWorksheet("MES任务明细");
        string[] jobHeaders = ["任务编号", "样品编号", "批次", "状态", "创建时间", "开始时间", "结束时间", "异常"];
        for (var c = 0; c < jobHeaders.Length; c++) jobs.Cell(1, c + 1).Value = jobHeaders[c];
        jobs.Columns(1, 3).Style.NumberFormat.Format = "@";
        for (var r = 0; r < snapshot.Jobs.Length; r++)
        {
            var j = snapshot.Jobs[r];
            string[] values = [j.Id, j.SampleId, j.Batch, j.Status, j.CreatedAt.ToString("O"),
                j.StartedAt?.ToString("O") ?? "未记录", j.CompletedAt?.ToString("O") ?? "未记录", j.Error ?? ""];
            for (var c = 0; c < values.Length; c++) jobs.Cell(r + 2, c + 1).Value = values[c];
        }
        Style(jobs, snapshot.Jobs.Length + 1, 8);
        var statistics = book.AddWorksheet("统计汇总");
        statistics.Cell(1, 1).Value = "项目"; statistics.Cell(1, 2).Value = "内容";
        var lines = Describe(snapshot).ToArray();
        for (var i = 0; i < lines.Length; i++)
        { statistics.Cell(i + 2, 1).Value = lines[i].Key; statistics.Cell(i + 2, 2).Value = lines[i].Value; }
        Style(statistics, lines.Length + 1, 2); statistics.Column(2).Width = 95;
        var ai = book.AddWorksheet("AI摘要与建议");
        ai.Cell(1, 1).Value = "AI 草稿，需人工核查；不替代测量结果与正式检测结论。";
        ai.Cell(2, 1).Value = summary?.Available == true ? summary.Text : summary?.Message ?? "AI 摘要未生成，统计数据不受影响。";
        ai.Cell(3, 1).Value = "模型：" + (summary?.Model ?? "未调用");
        ai.Cell(4, 1).Value = "依据：" + string.Join(", ", summary?.References ?? []);
        ai.Cell(5, 1).Value = "生成时间：" + (summary?.GeneratedAt.ToString("O") ?? "未生成");
        ai.Column(1).Width = 110; ai.Column(1).Style.Alignment.WrapText = true;
        ai.Row(1).Style.Font.Bold = true; ai.Row(2).Height = 240; ai.SheetView.FreezeRows(1);
        book.SaveAs(path);
    }

    internal static void Style(IXLWorksheet sheet, int rows, int columns)
    {
        sheet.Row(1).Style.Font.Bold = true;
        sheet.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#DCEAF7");
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, Math.Max(rows, 1), columns).SetAutoFilter();
        sheet.Columns(1, columns).Width = 24;
    }

    public static string[] MeasurementValues(Measurement m) => [m.RecordId, m.SampleId, m.Batch, m.Metric,
        m.Value.ToString(CultureInfo.InvariantCulture), m.Unit, m.MeasuredAt.ToString("O"), m.Source];

    public static void WriteCsv(string path, ReportSnapshot snapshot)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(string.Join(",", MeasurementFiles.Headers.Select(CsvCell)) + ",__mes_csv_text_v1");
        foreach (var row in snapshot.Measurements)
        {
            // All text columns get a reversible prefix; the marker distinguishes our encoding
            // from a user's ordinary CSV. Numeric values (including negatives) stay numeric.
            var values = MeasurementValues(row).Select((v, i) => i == 4 ? "\"" + v + "\"" : CsvCell("'" + v));
            writer.WriteLine(string.Join(",", values) + ",text-prefixed");
        }
    }

    public static string CsvCell(string value)
    {
        if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') ||
            value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@') ||
            value.StartsWith('\t') || value.StartsWith('\r')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static void WriteHtml(string path, ReportSnapshot snapshot, AiSummary? summary)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var html = new StringBuilder("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>实验报表</title><style>body{font-family:Arial,sans-serif;padding:24px}td,th{border:1px solid #ccc;padding:6px}table{border-collapse:collapse}pre{white-space:pre-wrap}</style><h1>实验报表（待人工确认）</h1><table>");
        foreach (var pair in Describe(snapshot)) html.Append($"<tr><th>{E(pair.Key)}</th><td>{E(pair.Value)}</td></tr>");
        html.Append("</table><h2>测量原始记录</h2><table><tr>");
        foreach (var h in MeasurementFiles.Headers) html.Append($"<th>{E(h)}</th>");
        html.Append("</tr>");
        foreach (var m in snapshot.Measurements)
        { html.Append("<tr>"); foreach (var v in MeasurementValues(m)) html.Append($"<td>{E(v)}</td>"); html.Append("</tr>"); }
        html.Append("</table><h2>AI 草稿／待核查</h2><pre>").Append(E(summary?.Available == true ? summary.Text : summary?.Message ?? "未生成"))
            .Append("</pre><p>模型：").Append(E(summary?.Model)).Append("；依据：").Append(E(string.Join(",", summary?.References ?? []))).Append("</p></html>");
        File.WriteAllText(path, html.ToString(), new UTF8Encoding(true));
    }

    public static IEnumerable<KeyValuePair<string, string>> Describe(ReportSnapshot snapshot)
    {
        var s = ReportRules.Calculate(snapshot.Jobs);
        yield return new("快照编号", snapshot.Id);
        yield return new("快照时间", snapshot.CapturedAt.ToString("O"));
        yield return new("筛选范围", $"{snapshot.Query.From:O} 至 {snapshot.Query.To:O}；批次：{snapshot.Query.Batch ?? "全部"}");
        yield return new("口径", ReportRules.Scope);
        yield return new("总任务数", s.Total.ToString(CultureInfo.InvariantCulture));
        yield return new("完成／失败／取消／其他", $"{s.Completed} / {s.Failed} / {s.Cancelled} / {s.Other}");
        yield return new("成功率", s.SuccessPercent?.ToString("0.##", CultureInfo.InvariantCulture) + (s.SuccessPercent.HasValue ? "%" : "不适用"));
        yield return new("完成任务平均耗时（秒）", s.AverageSeconds?.ToString("0.##", CultureInfo.InvariantCulture) ?? "未采集");
        yield return new("有效耗时记录／时间异常", $"{s.DurationCount} / {s.InvalidCompletedTimes}");
        yield return new("导入测量记录", snapshot.Measurements.Length.ToString(CultureInfo.InvariantCulture));
        yield return new("测量数据范围", "独立导入记录，不按任务创建日期自动裁剪；请核查样品、批次与测量时间。");
        foreach (var group in ReportRules.Aggregate(snapshot.Measurements))
            yield return new($"{group.Metric} [{group.Unit}]", $"数量 {group.Count}；最小 {group.Minimum}；最大 {group.Maximum}；平均 {group.Average}");
        foreach (var warning in snapshot.Warnings.Concat(ReportRules.MeasurementWarnings(snapshot.Measurements, snapshot.Jobs)))
            yield return new("数据提示", warning);
    }
}
