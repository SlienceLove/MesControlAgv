using System.Globalization;
using System.Text.Json;

namespace MesControlAgv.Contracts;

public sealed record ShineLabResultReviewItem(string SampleId, string Compound, string Code,
    string Observation, string Suggestions);

/// <summary>Advisory evidence checks, not an analytical validity decision. Never changes raw results.</summary>
public static class ShineLabResultReview
{
    public static IReadOnlyList<ShineLabResultReviewItem> Assess(ShineLabTaskSampleResponse sample)
    {
        var items = new List<ShineLabResultReviewItem>();
        void Add(string code, string observed, string suggestions, string compound = "—") =>
            items.Add(new(sample.SampleId, compound, code, observed, suggestions));
        if (sample.ResultConflict)
            Add("ResultConflict", "同一样品收到不一致结果", "核对客户端原始记录及多次上报；保留所有版本，不自动覆盖。");
        if (sample.ResultJson is null)
        {
            if (sample.TaskFinishObserved || sample.EndMissionObserved)
                Add("MissingResultAfterFinish", "已收到结束通知，但尚未收到 Result",
                    "核对 AQ/DA 数据、处理方法及客户端日志；不代表无峰或零浓度，不自动重跑。");
            return items;
        }
        try
        {
            using var json = JsonDocument.Parse(sample.ResultJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("compounds", out var compounds) || compounds.ValueKind != JsonValueKind.Array)
            {
                Add("InvalidResultStructure", "Result 缺少有效组分列表", "核对厂家上报格式；保留原文，不补零。");
                return items;
            }
            if (compounds.GetArrayLength() == 0)
                Add("EmptyCompounds", "已收到 Result 的组分列表为空", "不代表无峰；核对处理方法、校正曲线及客户端计算结果。");
            foreach (var compound in compounds.EnumerateArray())
            {
                var name = compound.ValueKind == JsonValueKind.Object && compound.TryGetProperty("name", out var n) ? n.ToString() : "未命名组分";
                var fields = new[] { "area", "height", "concent" };
                var values = fields.Select(f => compound.ValueKind == JsonValueKind.Object && compound.TryGetProperty(f, out var v) ? v : default).ToArray();
                if (values.Any(v => v.ValueKind == JsonValueKind.String && string.Equals(v.GetString()?.Trim(), "ND", StringComparison.OrdinalIgnoreCase)))
                    Add("NonQuantified", "客户端定量字段为 ND", "不能当作数值零；请厂家明确未检出/无效/未计算语义，并核对峰匹配及积分。", name);
                else if (values.Any(v => !TryNumber(v, out _)))
                    Add("IncompleteQuantitation", "峰面积、峰高或浓度缺失/非数值", "核对 Result 字段及客户端结果；不补零，不据此判定样品异常。", name);
                else if (values.All(v => TryNumber(v, out var number) && number == 0))
                    Add("AllZeroQuantitation", "峰面积、峰高、浓度均回传 0",
                        "可能原因（未确认）：样品余量/吸样不足、进样异常、仪器条件变化或处理/积分未成功；请核对 DA 是否为 ND。不能直接判为有效零浓度或样品耗尽。", name);
            }
        }
        catch (JsonException)
        {
            Add("MalformedResult", "Result 原文不是有效 JSON", "保留原始报文并核对解析/厂家格式；不要自动重跑。");
        }
        return items;
    }

    private static bool TryNumber(JsonElement value, out double number)
    {
        number = 0;
        return (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) ||
            value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) && double.IsFinite(number);
    }
}
