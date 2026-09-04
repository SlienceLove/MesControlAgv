using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tabName })
        {
            return;
        }

        var tab = FindTab(tabName);
        if (tab is not null)
        {
            MainTabs.SelectedItem = tab;
        }
    }

    /// <summary>Shows the monitor tab after an explicit external-run handoff.</summary>
    public void ShowWorkflowRunMonitor() =>
        MainTabs.SelectedItem = FindTab(nameof(WorkflowRunMonitorTab));

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabs.SelectedItem is not TabItem selectedTab)
        {
            return;
        }

        foreach (var button in FindVisualChildren<RadioButton>(NavigationSidebar))
        {
            if (button.Tag is string tabName && string.Equals(tabName, selectedTab.Name, StringComparison.Ordinal))
            {
                button.IsChecked = true;
                break;
            }
        }
    }

    private TabItem? FindTab(string name) =>
        MainTabs.Items.OfType<TabItem>()
            .FirstOrDefault(tab => string.Equals(tab.Name, name, StringComparison.Ordinal));

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is null)
        {
            yield break;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}

public sealed class MapBezierGeometryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MapEdgeViewModel edge)
        {
            return CreateGeometry(
                edge.X1,
                edge.Y1,
                edge.X2,
                edge.Y2,
                edge.Control1X,
                edge.Control1Y,
                edge.Control2X,
                edge.Control2Y);
        }

        if (value is MapPathSegmentViewModel segment)
        {
            return CreateGeometry(
                segment.X1,
                segment.Y1,
                segment.X2,
                segment.Y2,
                segment.Control1X,
                segment.Control1Y,
                segment.Control2X,
                segment.Control2Y);
        }

        return Geometry.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static PathGeometry CreateGeometry(
        double x1,
        double y1,
        double x2,
        double y2,
        double control1X,
        double control1Y,
        double control2X,
        double control2Y)
    {
        var figure = new PathFigure { StartPoint = new Point(x1, y1), IsClosed = false, IsFilled = false };
        if (double.IsNaN(control1X) || double.IsNaN(control1Y) ||
            double.IsNaN(control2X) || double.IsNaN(control2Y))
        {
            figure.Segments.Add(new LineSegment(new Point(x2, y2), isStroked: true));
        }
        else
        {
            figure.Segments.Add(new BezierSegment(
                new Point(control1X, control1Y),
                new Point(control2X, control2Y),
                new Point(x2, y2),
                isStroked: true));
        }

        return new PathGeometry([figure]);
    }
}

public sealed class MapRouteColorConverter : IValueConverter
{
    private static readonly SolidColorBrush BidirectionalBrush = new(Color.FromRgb(0x31, 0x5B, 0x87)); // 蓝色 - 双向
    private static readonly SolidColorBrush UnidirectionalBrush = new(Color.FromRgb(0xDC, 0x26, 0x26)); // 红色 - 单向

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool bidirectional)
        {
            return bidirectional ? BidirectionalBrush : UnidirectionalBrush;
        }
        return BidirectionalBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
