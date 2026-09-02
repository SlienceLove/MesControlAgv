using System.Globalization;
using System.IO;

namespace MesControlAgv.Wpf.Services;

/// <summary>
/// 解析离子色谱样品任务的 CSV/XLSX 模板。
/// 复用 <see cref="TabularFileReader"/> 的文件读取，但字段模型与 AGV 运输任务完全独立。
/// 既接受 ShineLab 原生 res/ExportData.csv 表头，也接受操作人员使用的中英文别名表头。
/// </summary>
public sealed class ShineLabSequenceParser
{
    private static readonly IReadOnlyDictionary<string, string[]> HeaderAliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["samplename"] = ["samplename", "样品名称", "样品名", "名称"],
            ["sampletype"] = ["sampletype", "样品类型", "类型"],
            ["samplelevel"] = ["samplelevel", "样品等级", "等级"],
            ["processmethod"] = ["processmethod", "处理方法"],
            ["clearcalibration"] = ["clearcalibration", "清除校正"],
            ["cyclecount"] = ["cyclecount", "循环次数"],
            ["injectionvolume"] = ["injectionvolume", "进样体积"],
            ["injectionunit"] = ["injectionunit", "进样单位"],
            ["blank"] = ["blank", "空白"],
            ["chromatographymethod"] = ["chromatographymethod", "色谱方法"]
        };

    /// <summary>
    /// 样品类型必填，且必须是 ShineLab 已验证的取值之一。
    /// 取值来自 res/ExportData.csv 现场导出模板。
    /// </summary>
    private static readonly string[] AllowedSampleTypes =
    [
        "基线", "空白", "质控", "加标",
        "未加标", "未知样品", "标准样品"
    ];

    private static readonly string[] AllowedInjectionUnits = ["μL", "uL", "mL"];

    // 现场导出证实 ShineLab 会把这些空字段沿用上一行。导入不可回滚，
    // 所以 MES 必须在下发前拿到明确值，不能把空值语义留给 ShineLab 猜测。
    private static readonly (string LogicalName, string DisplayName)[] RequiredExplicitFields =
    [
        ("samplelevel", "样品等级"),
        ("processmethod", "处理方法"),
        ("clearcalibration", "清除校正")
    ];

    public const string DefaultSampleName = "样品";
    public const string DefaultInjectionUnit = "μL";

    public ShineLabSequenceImportResult Parse(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var stream = File.OpenRead(filePath);
        return Parse(stream, Path.GetFileName(filePath));
    }

    public ShineLabSequenceImportResult Parse(Stream source, string fileName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        return ParseRows(TabularFileReader.ReadRows(source, fileName));
    }

    private static ShineLabSequenceImportResult ParseRows(IReadOnlyList<IReadOnlyList<string>> sourceRows)
    {
        var rows = sourceRows.ToList();
        var headerRowIndex = rows.FindIndex(row => row.Any(value => !string.IsNullOrWhiteSpace(value)));
        if (headerRowIndex < 0)
        {
            throw new InvalidDataException("样品任务文件没有表头行。");
        }

        var columns = ResolveColumns(rows[headerRowIndex]);
        var tasks = new List<ShineLabSampleTask>();
        var issues = new List<ShineLabSampleTaskIssue>();

        for (var index = headerRowIndex + 1; index < rows.Count; index++)
        {
            var row = rows[index];
            if (!row.Any(value => !string.IsNullOrWhiteSpace(value))) continue;

            var sourceRowNumber = index + 1;
            var sampleType = GetValue(row, columns, "sampletype");
            if (string.IsNullOrWhiteSpace(sampleType))
            {
                issues.Add(new ShineLabSampleTaskIssue(sourceRowNumber, "样品类型不能为空。"));
                continue;
            }

            if (!AllowedSampleTypes.Contains(sampleType, StringComparer.Ordinal))
            {
                issues.Add(new ShineLabSampleTaskIssue(
                    sourceRowNumber,
                    $"未知的样品类型‘{sampleType}’；允许值：{string.Join(" / ", AllowedSampleTypes)}。"));
                continue;
            }

            var explicitValues = RequiredExplicitFields.ToDictionary(
                field => field.LogicalName,
                field => GetValue(row, columns, field.LogicalName),
                StringComparer.Ordinal);
            var missingExplicitValue = false;
            foreach (var field in RequiredExplicitFields)
            {
                if (!string.IsNullOrWhiteSpace(explicitValues[field.LogicalName])) continue;

                issues.Add(new ShineLabSampleTaskIssue(
                    sourceRowNumber,
                    $"{field.DisplayName}不能为空；ShineLab 可能继承上一行，必须显式填写。"));
                missingExplicitValue = true;
            }
            if (missingExplicitValue) continue;

            var cycleCountText = GetValue(row, columns, "cyclecount");
            if (string.IsNullOrWhiteSpace(cycleCountText)) cycleCountText = "1";
            if (!int.TryParse(cycleCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cycleCount) || cycleCount < 1)
            {
                issues.Add(new ShineLabSampleTaskIssue(sourceRowNumber, $"无效的循环次数‘{cycleCountText}’。"));
                continue;
            }

            var injectionVolumeText = GetValue(row, columns, "injectionvolume");
            if (string.IsNullOrWhiteSpace(injectionVolumeText))
            {
                issues.Add(new ShineLabSampleTaskIssue(sourceRowNumber, "进样体积不能为空。"));
                continue;
            }

            if (!double.TryParse(injectionVolumeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var injectionVolume) || injectionVolume <= 0)
            {
                issues.Add(new ShineLabSampleTaskIssue(sourceRowNumber, $"无效的进样体积‘{injectionVolumeText}’。"));
                continue;
            }

            var injectionUnit = GetValue(row, columns, "injectionunit");
            if (string.IsNullOrWhiteSpace(injectionUnit)) injectionUnit = DefaultInjectionUnit;
            if (!AllowedInjectionUnits.Contains(injectionUnit, StringComparer.Ordinal))
            {
                issues.Add(new ShineLabSampleTaskIssue(
                    sourceRowNumber,
                    $"未知的进样单位‘{injectionUnit}’；允许值：{string.Join(" / ", AllowedInjectionUnits)}。"));
                continue;
            }

            var sampleName = GetValue(row, columns, "samplename");
            if (string.IsNullOrWhiteSpace(sampleName)) sampleName = DefaultSampleName;

            tasks.Add(new ShineLabSampleTask(
                sourceRowNumber,
                sampleName,
                sampleType,
                explicitValues["samplelevel"],
                explicitValues["processmethod"],
                explicitValues["clearcalibration"],
                cycleCountText.Trim(),
                injectionVolumeText.Trim(),
                injectionUnit,
                GetValue(row, columns, "blank"),
                GetValue(row, columns, "chromatographymethod")));
        }

        if (tasks.Count == 0 && issues.Count == 0)
        {
            issues.Add(new ShineLabSampleTaskIssue(0, "文件中没有可导入的样品任务行。"));
        }

        return new ShineLabSequenceImportResult(tasks, issues);
    }

    private static Dictionary<string, int> ResolveColumns(IReadOnlyList<string> header)
    {
        var normalizedHeaders = header.Select(TabularFileReader.NormalizeHeader).ToArray();
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var logicalName in HeaderAliases.Keys)
        {
            var aliases = HeaderAliases[logicalName]
                .Select(TabularFileReader.NormalizeHeader)
                .ToHashSet(StringComparer.Ordinal);
            var column = Array.FindIndex(normalizedHeaders, aliases.Contains);
            if (column >= 0) columns[logicalName] = column;
        }

        if (!columns.ContainsKey("sampletype"))
        {
            throw new InvalidDataException("缺少必需列：样品类型。");
        }

        if (!columns.ContainsKey("injectionvolume"))
        {
            throw new InvalidDataException("缺少必需列：进样体积。");
        }

        return columns;
    }

    private static string GetValue(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> columns, string logicalName)
    {
        return columns.TryGetValue(logicalName, out var index) && index < row.Count
            ? (row[index] ?? string.Empty).Trim()
            : string.Empty;
    }
}
