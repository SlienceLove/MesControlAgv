using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MesControlAgv.Wpf.Services;

/// <summary>
/// 生成 ShineLab 可直接“从CSV导入”的样品任务 CSV。
/// 输出严格对齐现场导出的 res/ExportData.csv：13 列规范表头、行尾多一个逗号、UTF-8 无 BOM、CRLF。
/// 序号从 1 连续编号；选择和数据名称留空，由 ShineLab 自行生成。
/// </summary>
public static class ShineLabCsvWriter
{
    public static readonly IReadOnlyList<string> CanonicalColumns =
    [
        "序号", "选择", "样品名称", "样品类型", "样品等级", "处理方法", "清除校正",
        "循环次数", "进样体积", "进样单位", "空白", "数据名称", "色谱方法"
    ];

    /// <summary>
    /// 表头与数据行都以逗号结尾，与 ShineLab 现场导出保持一致。
    /// </summary>
    public static string CanonicalHeaderLine => string.Join(",", CanonicalColumns) + ",";

    public static string Build(IReadOnlyList<ShineLabSampleTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Count == 0)
        {
            throw new InvalidOperationException("没有样品任务可生成 ShineLab CSV。");
        }

        var builder = new StringBuilder();
        builder.Append(CanonicalHeaderLine).Append("\r\n");

        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            EnsureDeterministicFields(task);
            var fields = new[]
            {
                (index + 1).ToString(CultureInfo.InvariantCulture),
                string.Empty,
                task.SampleName,
                task.SampleType,
                QuoteSampleLevel(task.SampleLevel),
                task.ProcessMethod,
                task.ClearCalibration,
                task.CycleCount,
                task.InjectionVolume,
                task.InjectionUnit,
                task.Blank,
                string.Empty,
                task.ChromatographyMethod
            };

            builder.Append(string.Join(",", fields.Select(Escape))).Append(',').Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// UTF-8 无 BOM 写入。ShineLab 现场导出的模板没有 BOM，比对内核也按无 BOM 严格读取。
    /// </summary>
    public static void Write(string path, IReadOnlyList<ShineLabSampleTask> tasks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var content = Build(tasks);
        File.WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
    }

    public static string ComputeSha256(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>
    /// 样品等级按 ShineLab 导出习惯加引号，保留 "07" 这类前导零的文本语义。
    /// </summary>
    private static string QuoteSampleLevel(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : "\"" + value.Replace("\"", "\"\"") + "\"";

    private static void EnsureDeterministicFields(ShineLabSampleTask task)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(task.SampleLevel)) missing.Add("样品等级");
        if (string.IsNullOrWhiteSpace(task.ProcessMethod)) missing.Add("处理方法");
        if (string.IsNullOrWhiteSpace(task.ClearCalibration)) missing.Add("清除校正");
        if (missing.Count == 0) return;

        throw new InvalidOperationException(
            $"源文件第 {task.SourceRowNumber} 行缺少显式字段：{string.Join("、", missing)}。ShineLab 可能继承上一行，已拒绝生成 CSV。");
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2) return value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
