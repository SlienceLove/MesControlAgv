using System.IO;
using MesControlAgv.Contracts.Samples;

namespace MesControlAgv.Wpf.Services;

public sealed record SampleImportIssue(int SourceRowNumber, string Message);

public sealed record SampleImportParseResult(
    IReadOnlyList<SampleImportRowRequest> Rows,
    IReadOnlyList<SampleImportIssue> Issues);

/// <summary>
/// Parses the operator sample template without a third-party Excel dependency.
/// Both CSV and XLSX are supported through <see cref="TabularFileReader"/>.
/// </summary>
public sealed class SampleImportParser
{
    private static readonly IReadOnlyDictionary<string, string[]> HeaderAliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["sampleid"] = ["sampleid", "samplecode", "样品id", "样品编号", "样品编码"],
            ["barcode"] = ["barcode", "条码", "样品条码", "扫码条码"],
            ["batch"] = ["batch", "batchid", "samplebatchid", "批次", "样品批次", "批次号"],
            ["source"] = ["source", "sourcelocation", "来源库位", "来源位置", "起始库位"],
            ["position"] = ["position", "containerposition", "容器位", "容器位置", "管位"],
            ["runid"] = ["runid", "workflowrunid", "运行id", "流程runid", "运行编号"]
        };

    private static readonly string[] RequiredColumns = ["sampleid", "barcode", "batch", "source"];

    public SampleImportParseResult Parse(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var stream = File.OpenRead(filePath);
        return Parse(stream, Path.GetFileName(filePath));
    }

    public SampleImportParseResult Parse(Stream source, string fileName)
    {
        var sourceRows = TabularFileReader.ReadRows(source, fileName);
        var headerRowIndex = sourceRows.ToList().FindIndex(row => row.Any(value => !string.IsNullOrWhiteSpace(value)));
        if (headerRowIndex < 0) throw new InvalidDataException("样品导入文件不包含表头。");

        var columns = ResolveColumns(sourceRows[headerRowIndex]);
        var rows = new List<SampleImportRowRequest>();
        var issues = new List<SampleImportIssue>();
        var sampleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var barcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = headerRowIndex + 1; index < sourceRows.Count; index++)
        {
            var sourceRow = sourceRows[index];
            if (!sourceRow.Any(value => !string.IsNullOrWhiteSpace(value))) continue;
            var rowNumber = index + 1;
            var sampleId = GetValue(sourceRow, columns, "sampleid");
            var barcode = GetValue(sourceRow, columns, "barcode");
            var batch = GetValue(sourceRow, columns, "batch");
            var sourceLocation = GetValue(sourceRow, columns, "source");
            if (string.IsNullOrWhiteSpace(sampleId) || string.IsNullOrWhiteSpace(barcode) ||
                string.IsNullOrWhiteSpace(batch) || string.IsNullOrWhiteSpace(sourceLocation))
            {
                issues.Add(new SampleImportIssue(rowNumber, "样品 ID、条码、样品批次、来源库位均为必填。"));
                continue;
            }

            if (!sampleIds.Add(sampleId))
            {
                issues.Add(new SampleImportIssue(rowNumber, $"文件内重复样品 ID：{sampleId}。"));
                continue;
            }

            if (!barcodes.Add(barcode))
            {
                issues.Add(new SampleImportIssue(rowNumber, $"文件内重复条码：{barcode}。"));
                continue;
            }

            var runIdText = GetValue(sourceRow, columns, "runid");
            Guid? runId = null;
            if (!string.IsNullOrWhiteSpace(runIdText))
            {
                if (!Guid.TryParse(runIdText, out var parsedRunId))
                {
                    issues.Add(new SampleImportIssue(rowNumber, $"RunId 不是有效 GUID：{runIdText}。"));
                    continue;
                }
                runId = parsedRunId;
            }

            rows.Add(new SampleImportRowRequest
            {
                RowNumber = rowNumber,
                SampleId = sampleId,
                Barcode = barcode,
                SampleBatchId = batch,
                SourceLocation = sourceLocation,
                ContainerPosition = NullIfEmpty(GetValue(sourceRow, columns, "position")),
                RunId = runId
            });
        }

        return new SampleImportParseResult(rows, issues);
    }

    private static Dictionary<string, int> ResolveColumns(IReadOnlyList<string> header)
    {
        var normalized = header.Select(TabularFileReader.NormalizeHeader).ToArray();
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in HeaderAliases)
        {
            var aliases = pair.Value.Select(TabularFileReader.NormalizeHeader).ToHashSet(StringComparer.Ordinal);
            var index = Array.FindIndex(normalized, aliases.Contains);
            if (index >= 0) columns[pair.Key] = index;
        }

        var missing = RequiredColumns.Where(item => !columns.ContainsKey(item)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"样品导入缺少必填列：{string.Join("、", missing)}。");
        return columns;
    }

    private static string GetValue(
        IReadOnlyList<string> row,
        IReadOnlyDictionary<string, int> columns,
        string name) =>
        columns.TryGetValue(name, out var index) && index < row.Count
            ? row[index].Trim()
            : string.Empty;

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
