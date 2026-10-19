using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.VisualBasic.FileIO;

namespace MesControlAgv.Reporting;

public static class MeasurementFiles
{
    public static readonly string[] Headers = ["记录编号", "样品编号", "批次", "检测项目", "结果", "单位", "测量时间", "来源"];
    private static readonly string[] Keys = ["record_id", "sample_id", "batch_id", "metric", "value", "unit", "measured_at", "source"];

    public static ImportPreview Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > ReportRules.MaxFileBytes) throw new InvalidDataException("文件不得超过 5 MB。");
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        var rows = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xlsx" => ReadExcel(stream),
            ".csv" => ReadCsv(stream),
            _ => throw new InvalidDataException("仅支持 .xlsx 和 UTF-8 .csv，不支持旧版 .xls。")
        };
        return Validate(rows, hash);
    }

    private static List<string[]> ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, leaveOpen: true);
        using var parser = new TextFieldParser(reader) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var rows = new List<string[]>();
        while (!parser.EndOfData)
        {
            if (rows.Count > ReportRules.MaxRows) throw new InvalidDataException("数据不得超过 10,000 行。");
            rows.Add(parser.ReadFields() ?? []);
        }
        if (rows.Count > 0 && rows[0].Length == 9 && rows[0][8] == "__mes_csv_text_v1")
        {
            rows[0] = rows[0][..8];
            for (var i = 1; i < rows.Count; i++)
            {
                if (rows[i].Length != 9 || rows[i][8] != "text-prefixed" ||
                    rows[i].Take(8).Where((_, c) => c != 4).Any(v => !v.StartsWith('\'')))
                    throw new InvalidDataException("CSV 导出编码损坏，请重新导出或使用标准模板。");
                rows[i] = rows[i].Take(8).Select((v, c) => c == 4 ? v : v[1..]).ToArray();
            }
        }
        return rows;
    }

    private static List<string[]> ReadExcel(Stream stream)
    {
        // Bound expansion before ClosedXML loads XML; no macros, formulas or external links.
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
        {
            if (zip.Entries.Count > 1000 || zip.Entries.Sum(e => e.Length) > 30L * 1024 * 1024)
                throw new InvalidDataException("Excel 解压后过大，请拆分文件。");
            if (zip.Entries.Any(e => e.FullName.Contains("externalLinks/", StringComparison.OrdinalIgnoreCase) ||
                                     e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("不接受宏或外部链接，请粘贴为纯数据后导入。");
        }
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.FirstOrDefault(x => x.Name == "原始记录") ?? workbook.Worksheets.First();
        var count = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var columns = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        if (count > ReportRules.MaxRows + 1 || columns > 10 ||
            (columns > 8 && (sheet.Cell(1, 9).GetString() != "文件SHA256" || sheet.Cell(1, 10).GetString() != "来源行号")))
            throw new InvalidDataException("请使用 8 列模板或本软件导出的报表，数据不得超过 10,000 行。");
        var rows = new List<string[]>();
        for (var r = 1; r <= count; r++)
        {
            var row = new string[8];
            for (var c = 1; c <= 8; c++)
            {
                var cell = sheet.Cell(r, c);
                if (cell.HasFormula) throw new InvalidDataException($"第 {r} 行含公式，请粘贴为值后导入。");
                if (r > 1 && (c <= 3 || c == 7) && !cell.IsEmpty() && cell.DataType != XLDataType.Text)
                    throw new InvalidDataException($"第 {r} 行：编号和带时区的时间必须为文本，请使用模板，避免丢失前导零。");
                row[c - 1] = cell.DataType == XLDataType.Number
                    ? cell.GetDouble().ToString("R", CultureInfo.InvariantCulture) : cell.GetString();
            }
            rows.Add(row);
        }
        return rows;
    }

    public static ImportPreview Validate(IReadOnlyList<string[]> rows, string hash)
    {
        if (rows.Count == 0) throw new InvalidDataException("文件没有表头。");
        if (rows.Count > ReportRules.MaxRows + 1) throw new InvalidDataException("数据不得超过 10,000 行。");
        if (rows[0].Length != 8 || !rows[0].Select((x, i) => x.Trim() == Headers[i] || x.Trim() == Keys[i]).All(x => x))
            throw new InvalidDataException("表头不匹配，请使用提供的 Excel 模板（中英文列名均支持）。");
        var records = new List<Measurement>(); var issues = new List<ImportIssue>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < rows.Count; i++)
        {
            var r = rows[i].Select(x => x.Trim()).ToArray();
            if (r.All(string.IsNullOrEmpty)) continue;
            if (r.Length != 8 || r.Any(x => x.Length == 0 || x.Length > 256))
            { issues.Add(new(i + 1, "必须填写 8 个字段，每个字段最多 256 字符。")); continue; }
            if (!ids.Add(r[0])) { issues.Add(new(i + 1, "记录编号重复。")); continue; }
            if (!decimal.TryParse(r[4], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture, out var value) || value > 1_000_000_000_000m || value < -1_000_000_000_000m)
            { issues.Add(new(i + 1, "结果必须为有效数值，使用小数点，绝对值不超过 10^12。")); continue; }
            if (!Regex.IsMatch(r[6], @"(Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase) ||
                !DateTimeOffset.TryParse(r[6], CultureInfo.InvariantCulture, DateTimeStyles.None, out var measured))
            { issues.Add(new(i + 1, "测量时间必须带时区，例如 2026-09-21T10:00:00+08:00。")); continue; }
            records.Add(new(r[0], r[1], r[2], r[3], value, r[5], measured, r[7], hash, i + 1));
        }
        return new(records.ToArray(), issues.ToArray(), hash);
    }

    public static void WriteTemplate(string path)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("原始记录");
        for (var c = 1; c <= 8; c++) sheet.Cell(1, c).Value = Headers[c - 1];
        sheet.Columns(1, 3).Style.NumberFormat.Format = "@";
        sheet.Column(7).Style.NumberFormat.Format = "@";
        ReportFiles.Style(sheet, 1, 8);
        var help = workbook.AddWorksheet("填写说明");
        help.Cell(1, 1).Value = "一行一条结果；编号按文本填写，结果为数值，时间含时区；不接受公式。";
        help.Cell(2, 1).Value = "时间示例：2026-09-21T10:00:00+08:00；单位必须明确，原文件不会被修改。";
        help.Column(1).Width = 100;
        workbook.SaveAs(path);
    }
}
