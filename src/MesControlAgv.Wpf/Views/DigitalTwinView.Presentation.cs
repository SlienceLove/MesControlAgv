using System.Windows;
using MesControlAgv.Wpf.DigitalTwin;

namespace MesControlAgv.Wpf.Views;

public partial class DigitalTwinView
{
    private Action<bool?>? _fullScreenHost;
    private string? _presentationPoseWarning = "等待位置更新";
    public bool IsPresentationMode { get; private set; }

    public void SetFullScreenHost(Action<bool?> request)
    {
        _fullScreenHost = request;
        FullScreenButton.IsEnabled = true;
    }
    public void SetPresentationMode(bool enabled)
    {
        if (IsPresentationMode == enabled) return;
        IsPresentationMode = enabled;
        DetailedHeader.Visibility = DetailedReadings.Visibility = DetailedFooter.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        PresentationToolbar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        LayoutRoot.Margin = new Thickness(enabled ? 0 : 12);
        UpdatePresentationWarning();
        SendTelemetry();
    }
    private void FullScreen_Click(object sender, RoutedEventArgs e) => _fullScreenHost?.Invoke(true);
    private void ExitFullScreen_Click(object sender, RoutedEventArgs e) => _fullScreenHost?.Invoke(false);
    private void UpdatePresentationWarning()
    {
        var warning = !State.IsReady ? "三维尚未就绪 · " + State.Status : _presentationPoseWarning;
        PresentationWarning.Text = warning ?? "";
        PresentationWarning.Visibility = IsPresentationMode && !string.IsNullOrEmpty(warning) ? Visibility.Visible : Visibility.Collapsed;
    }
    private static string CompactStatus(TwinReading reading) => reading.Condition switch
    {
        TwinCondition.Loading => "读取中", TwinCondition.Online => "在线", TwinCondition.Idle => "空闲",
        TwinCondition.Running => "运行中", TwinCondition.Paused => "已暂停", TwinCondition.Fault => "故障",
        TwinCondition.Offline => "离线", TwinCondition.Unavailable => "读取失败", TwinCondition.Stale => "数据过期",
        _ => "状态未知"
    } + (reading.Partial ? "（部分数据缺失）" : "");
}
