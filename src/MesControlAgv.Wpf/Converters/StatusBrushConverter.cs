using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MesControlAgv.Wpf.Converters;

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString()?.Trim() ?? string.Empty;
        if (ContainsAny(text, "失败", "错误", "异常", "不可用", "离线", "禁用", "阻塞"))
            return Brush("#F04438");
        if (ContainsAny(text, "等待", "待", "暂停", "确认", "未知", "尚未", "未读取", "过期", "暂无", "未加载", "已取消", "警告"))
            return Brush("#F79009");
        if (ContainsAny(text, "运行", "执行", "派发", "处理中", "读取中", "刷新中"))
            return Brush("#1677FF");
        if (ContainsAny(text, "完成", "成功", "在线", "正常", "空闲", "已连接", "已更新"))
            return Brush("#12B76A");
        if (ContainsAny(text, "取消", "释放", "未开放"))
            return Brush("#667085");
        return Brush("#98A2B3");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool ContainsAny(string text, params string[] values) => values.Any(text.Contains);

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
