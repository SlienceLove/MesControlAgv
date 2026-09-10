using System.Globalization;
using System.IO;

namespace MesControlAgv.Wpf.Services;

/// <summary>
/// Parses the direct TCP sample sheet. It intentionally has a stricter schema
/// than the legacy append-import parser so no position/channel/method value is
/// silently inherited or defaulted before a Config preview is built.
/// </summary>
public sealed class ShineLabDirectSequenceParser
{
    private static readonly IReadOnlyDictionary<string, string[]> HeaderAliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["sampleid"] = ["sampleid", "样品编号", "样品ID"],
            ["samplename"] = ["samplename", "样品名称"],
            ["sampletype"] = ["sampletype", "样品类型"],
            ["samplelevel"] = ["samplelevel", "样品等级"],
            ["position"] = ["position", "样品位", "进样位置", "盘位"],
            ["mpos"] = ["mpos", "机械位置", "托盘位置"],
            ["channel"] = ["channel", "通道"],
            ["instrumentmethod"] = ["instrumentmethod", "进样方法", "仪器方法"],
            ["processingmethod"] = ["processingmethod", "处理方法", "积分方法"],
            ["detectionmethod"] = ["detectionmethod", "检测方法", "检测方式"],
            ["injectionvolume"] = ["injectionvolume", "进样体积"],
            ["injectionvolumeunit"] = ["injectionvolumeunit", "进样单位", "进样体积单位"],
            ["cyclecount"] = ["cyclecount", "循环次数"]
        };

    private static readonly IReadOnlyDictionary<string, int> SampleTypeCodes =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["0"] = 0, ["未知样"] = 0, ["未知样品"] = 0,
            ["1"] = 1, ["标准样"] = 1, ["标准"] = 1,
            ["2"] = 2, ["基线"] = 2, ["基线样"] = 2,
            ["3"] = 3, ["空白"] = 3, ["空白样"] = 3,
            ["4"] = 4, ["质控"] = 4, ["质控样"] = 4,
            ["5"] = 5, ["加标"] = 5, ["加标样"] = 5
        };

    private static readonly string[] AllowedUnits = ["uL", "μL", "mL"];

    public ShineLabDirectSequenceResult Parse(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var stream = File.OpenRead(filePath);
        return Parse(stream, Path.GetFileName(filePath));
    }

    public ShineLabDirectSequenceResult Parse(Stream source, string fileName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return ParseRows(TabularFileReader.ReadRows(source, fileName));
    }

    private static ShineLabDirectSequenceResult ParseRows(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var headerIndex = rows.ToList().FindIndex(row => row.Any(value => !string.IsNullOrWhiteSpace(value)));
        if (headerIndex < 0)
            return new([], [new(0, "直连样品表没有表头。")], false);

        var columns = ResolveColumns(rows[headerIndex]);
        if (columns.Count == 0)
            return new([], [new(0, "缺少直连样品表字段；需要 sampleID、sampleType、position、mPos、channel、方法和体积列。")], false);

        var issues = new List<ShineLabDirectSampleTaskIssue>();
        var tasks = new List<ShineLabDirectSampleTask>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = headerIndex + 1; index < rows.Count; index++)
        {
            var row = rows[index];
            if (!row.Any(value => !string.IsNullOrWhiteSpace(value))) continue;
            var rowNumber = index + 1;

            var sampleId = Get(row, columns, "sampleid");
            var sampleName = Get(row, columns, "samplename");
            var sampleTypeText = Get(row, columns, "sampletype");
            var mPos = Get(row, columns, "mpos");
            var channel = Get(row, columns, "channel").ToUpperInvariant();
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(sampleId)) errors.Add("sampleID 不能为空");
            if (!string.IsNullOrWhiteSpace(sampleId) && !usedIds.Add(sampleId)) errors.Add($"sampleID '{sampleId}' 重复");
            if (string.IsNullOrWhiteSpace(sampleName)) errors.Add("sampleName 不能为空");
            if (!SampleTypeCodes.TryGetValue(sampleTypeText, out var sampleTypeCode)) errors.Add($"未知 sampleType '{sampleTypeText}'");
            if (!int.TryParse(Get(row, columns, "position"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var position) || position <= 0)
                errors.Add("position 必须是正整数");
            if (string.IsNullOrWhiteSpace(mPos)) errors.Add("mPos 不能为空");
            if (channel is not ("A" or "B")) errors.Add("channel 必须是 A 或 B");
            var instrumentMethod = Get(row, columns, "instrumentmethod");
            var processingMethod = Get(row, columns, "processingmethod");
            if (string.IsNullOrWhiteSpace(instrumentMethod)) errors.Add("instrumentMethod 不能为空");
            if (string.IsNullOrWhiteSpace(processingMethod)) errors.Add("processingMethod 不能为空");
            var injectionText = Get(row, columns, "injectionvolume");
            if (!decimal.TryParse(injectionText, NumberStyles.Number, CultureInfo.InvariantCulture, out var injectionVolume) || injectionVolume <= 0)
                errors.Add("injectionVolume 必须是正数");
            var unit = Get(row, columns, "injectionvolumeunit");
            if (!AllowedUnits.Contains(unit, StringComparer.OrdinalIgnoreCase)) errors.Add("injectionVolumeUnit 必须是 uL、μL 或 mL");
            var cycleText = Get(row, columns, "cyclecount");
            if (string.IsNullOrWhiteSpace(cycleText)) cycleText = "1";
            if (!int.TryParse(cycleText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cycleCount) || cycleCount <= 0)
                errors.Add("cycleCount 必须是正整数");

            if (errors.Count > 0)
            {
                issues.Add(new(rowNumber, string.Join("；", errors)));
                continue;
            }

            tasks.Add(new(
                rowNumber,
                sampleId,
                sampleName,
                sampleTypeText,
                sampleTypeCode,
                Get(row, columns, "samplelevel"),
                position,
                mPos,
                channel,
                instrumentMethod,
                processingMethod,
                NullIfEmpty(Get(row, columns, "detectionmethod")),
                injectionVolume,
                unit,
                cycleCount));
        }

        if (tasks.Select(task => task.Channel).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            issues.Add(new(0, "一次 Config 只能包含同一 channel 的样品；请按 A/B 通道拆分文件。"));

        return new(tasks, issues, true);
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

        var required = new[] { "sampleid", "samplename", "sampletype", "position", "mpos", "channel", "instrumentmethod", "processingmethod", "injectionvolume", "injectionvolumeunit" };
        if (required.Any(key => !columns.ContainsKey(key))) return [];
        return columns;
    }

    private static string Get(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> columns, string key) =>
        columns.TryGetValue(key, out var index) && index < row.Count ? row[index].Trim() : string.Empty;

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
