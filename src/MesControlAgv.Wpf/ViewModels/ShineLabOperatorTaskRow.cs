using MesControlAgv.Contracts;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>Operator projection. Protocol identity and raw events remain in task/audit storage.</summary>
public sealed record ShineLabOperatorTaskRow(ShineLabTaskResponse Task, ShineLabTaskDetailResponse? Detail)
{
    public string SampleNames => Detail?.Samples is { Count: > 0 } samples
        ? string.Join("、", samples.Select(s => s.SampleName)) : "历史样品记录";
    public string SourceNumbers => Detail?.Samples is { Count: > 0 } samples
        ? string.Join("、", samples.Select(s => s.SourceSampleId)) : "—";
    public string CreatedTime => Task.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
    public string State => DescribeState(Task.Status);
    public string ResultState => Detail?.Samples is not { Count: > 0 } samples ? "暂无结果" :
        samples.Any(s => ShineLabResultReview.Assess(s).Count > 0) ? "结果待复核" :
        samples.All(s => s.ResultJson is not null) ? "结果已收到" :
        samples.Any(s => s.ResultJson is not null) ? "收到部分结果" : "等待结果";

    public static string DescribeState(string status) => status switch
    {
        "Created" => "待下发", "Configuring" => "正在下发", "Configured" => "已下发，待运行",
        "Commanding" => "正在请求操作", "Accepted" => "启动已受理", "Running" => "运行中",
        "Completed" => "已结束", "AwaitingCompletion" => "结束状态待确认", "Observed" => "已收到运行反馈",
        "Stopping" => "停止确认中", "Failed" => "操作失败", "Error" => "设备异常",
        "Unknown" => "执行情况待确认", _ => "状态待确认"
    };
}
