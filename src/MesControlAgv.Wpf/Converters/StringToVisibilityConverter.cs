using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MesControlAgv.Wpf.Converters;

/// <summary>字符串非空时显示，空时隐藏。</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return !string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 可见性只是字符串状态的投影，反向绑定没有可靠的字符串值可恢复。
        // 返回 DoNothing 可避免误设为 TwoWay 时把 UI 异常传播到调度线程。
        return Binding.DoNothing;
    }
}
