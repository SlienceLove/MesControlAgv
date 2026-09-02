using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MesControlAgv.Wpf.Converters;

public sealed class StartupDiagnosticBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? string.Empty;
        var color = text.Contains("错误", StringComparison.Ordinal) || text.Contains("检查失败", StringComparison.Ordinal) || text.Contains("离线阻断", StringComparison.Ordinal) || text.Contains("移除", StringComparison.Ordinal) || text.Contains("无法读取", StringComparison.Ordinal) || text.Contains("不支持", StringComparison.Ordinal)
            ? "#F04438"
            : text.Contains("警告", StringComparison.Ordinal) ||
              text.Contains("可能", StringComparison.Ordinal) ||
              text.Contains("未知", StringComparison.Ordinal) ||
              text.Contains("需现场确认", StringComparison.Ordinal) ||
              text.Contains("已变化", StringComparison.Ordinal) ||
              text.Contains("新增", StringComparison.Ordinal) ||
              text.Contains("需升级", StringComparison.Ordinal) ||
              text.Contains("清理候选", StringComparison.Ordinal)
                ? "#F79009"
            : text.Contains("正常", StringComparison.Ordinal) ||
                  text.Contains("通过", StringComparison.Ordinal) ||
                  text.Contains("离线已知", StringComparison.Ordinal) ||
                  text.Contains("未变化", StringComparison.Ordinal) ||
                  text.Contains("当前格式", StringComparison.Ordinal) ||
                  text.Equals("保留", StringComparison.Ordinal) ||
                  text.StartsWith("禁用", StringComparison.Ordinal)
                    ? "#12B76A"
                    : "#667085";
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
