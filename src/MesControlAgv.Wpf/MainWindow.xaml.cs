using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf;

public partial class MainWindow : Window
{
    private bool _twinStartupApplied;
    private AccessClient? _accessClient;
    private AccessUser? _accessUser;
    private bool _sessionEnding;
    private bool _reauthenticationPending;
    private readonly HashSet<Window> _watchedSessionDialogs = [];
    public event EventHandler? SessionEnded;
    private CancellationTokenSource? _sessionMonitorCancellation;
    public MainWindow()
    {
        InitializeComponent();
        InitializeTwinFullScreen();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => _sessionMonitorCancellation?.Cancel();
        PageFrame.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(AccessPreviewMouseDown), true);
        PageFrame.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(AccessPreviewKeyDown), true);

        // The grouped sidebar's checked state is only a visual affordance and
        // does not raise a Click event during startup. Keep the content host
        // deterministic even when WPF has not selected an item yet.
        EnsureInitialTabSelection();
    }

    public void SetAccessSession(AccessClient client, AccessUser user, bool demo = false)
    {
        _accessClient = client; _accessUser = user;
        UpdateAccessPresentation(user);
        _sessionMonitorCancellation?.Cancel();
        _sessionMonitorCancellation = new CancellationTokenSource();
        _ = new AccessSessionMonitor(_accessClient.GetCurrentUserAsync)
            .RunAsync(UpdateAccessPresentation, Reauthenticate, _sessionMonitorCancellation.Token);
    }

    private async void AccessLogout_Click(object sender, RoutedEventArgs e)
    {
        if (_accessClient is null || _sessionEnding || !AccessLogoutButton.IsEnabled) return;
        if (MessageBox.Show(this, "退出登录将清除当前页面中未保存的内容，后台任务继续运行。是否继续？", "退出登录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        AccessLogoutButton.IsEnabled = false;
        _sessionMonitorCancellation?.Cancel();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _accessClient.LogoutAsync(timeout.Token);
        }
        catch { /* Discard the local token even if the server is unavailable. */ }
        Reauthenticate();
    }

    private void AccessManage_Click(object sender, RoutedEventArgs e)
    {
        if (_accessClient is null || _accessUser?.Role != AccessRoles.Administrator) return;
        try
        {
            var dialog = new UserManagementWindow(_accessClient) { Owner = this };
            dialog.ShowDialog();
            if (dialog.SessionInvalidated || _reauthenticationPending) Reauthenticate();
        }
        catch (Exception ex) { MessageBox.Show(this, $"用户管理打开失败：{ex.Message}", "用户权限管理", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void AccessPassword_Click(object sender, RoutedEventArgs e)
    {
        if (_accessClient is null) return;
        var dialog = new AccountEditorWindow(_accessClient, AccountEditorMode.ChangePassword) { Owner = this };
        dialog.ShowDialog();
        if (dialog.SessionInvalidated || _reauthenticationPending) Reauthenticate();
    }

    private void Reauthenticate()
    {
        if (_sessionEnding) return;
        _sessionMonitorCancellation?.Cancel();
        _accessClient?.ClearSession();
        _reauthenticationPending = true;
        // Do not tear down an owned editor while its HTTP response is being handled.
        if (OwnedWindows.Count > 0)
        {
            foreach (Window dialog in OwnedWindows)
                if (_watchedSessionDialogs.Add(dialog))
                    dialog.Closed += (_, _) => Dispatcher.BeginInvoke(new Action(Reauthenticate));
            AccessUserText.Text = "会话已失效，请关闭弹窗后重新登录";
            PageFrame.IsEnabled = false;
            return;
        }
        _sessionEnding = true;
        _accessUser = null;
        IsEnabled = false;
        Hide();
        SessionEnded?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateAccessPresentation(AccessUser user)
    {
        if (_sessionEnding || _reauthenticationPending) return;
        if (_accessUser is not null && (_accessUser.Id != user.Id || _accessUser.Role != user.Role || !user.Enabled))
        { Reauthenticate(); return; }
        _accessUser = user;
        AccessUserText.Text = $"{user.DisplayName} · {AccessRoles.Display(user.Role)}";
        AccessUserText.ToolTip = AccessUiPolicy.Summary(user.Role);
        AccessManageButton.Visibility = user.Role == AccessRoles.Administrator ? Visibility.Visible : Visibility.Collapsed;
        AccessPasswordButton.Visibility = Visibility.Visible;
        AccessLogoutButton.Visibility = Visibility.Visible;
        foreach (var button in FindVisualChildren<RadioButton>(NavigationSidebar))
            if (button.Tag is string page) button.IsEnabled = AccessUiPolicy.CanNavigate(user.Role, page);
        Dispatcher.BeginInvoke(new Action(ApplyPageAccess), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void AccessPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_accessUser?.Role != AccessRoles.Viewer || e.OriginalSource is not DependencyObject source) return;
        var button = FindAncestor<ButtonBase>(source);
        if (button is null or RadioButton or RepeatButton or DataGridColumnHeader) return;
        var label = button?.Content switch { TextBlock text => text.Text, string text => text, _ => null };
        if (!AccessUiPolicy.CanUseAction(_accessUser.Role, label))
        {
            e.Handled = true;
            MessageBox.Show(this, "只读账号不能执行此操作。", "当前账号权限", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void AccessPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_accessUser?.Role != AccessRoles.Viewer || e.Key is not (Key.Enter or Key.Space) || e.OriginalSource is not DependencyObject source) return;
        var button = FindAncestor<Button>(source);
        if (button is null) return;
        var label = button.Content switch { TextBlock text => text.Text, string text => text, _ => null };
        if (!AccessUiPolicy.CanUseAction(_accessUser.Role, label)) e.Handled = true;
    }

    private void ApplyPageAccess()
    {
        if (_accessUser is null || _sessionEnding) return;
        foreach (var nav in FindVisualChildren<RadioButton>(NavigationSidebar))
            if (nav.Tag is string page) nav.IsEnabled = AccessUiPolicy.CanNavigate(_accessUser.Role, page);
        if (MainTabs.SelectedItem is TabItem selected && !AccessUiPolicy.CanNavigate(_accessUser.Role, selected.Name))
            MainTabs.SelectedItem = KpiDashboardTab;
        if (_accessUser.Role != AccessRoles.Viewer) return;
        foreach (var button in FindVisualChildren<Button>(PageFrame))
        {
            var label = button.Content switch { TextBlock text => text.Text, string text => text, _ => null };
            if (!AccessUiPolicy.CanUseAction(_accessUser.Role, label)) button.IsEnabled = false;
        }
        foreach (var grid in FindVisualChildren<DataGrid>(PageFrame)) grid.IsReadOnly = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureInitialTabSelection();
        if (DataContext is MainViewModel && string.Equals(Environment.GetEnvironmentVariable("WPF_START_PAGE"), "ShineLabControl", StringComparison.Ordinal))
            MainTabs.SelectedItem = ShineLabDeviceStatusTab;
        ApplyPageAccess();
        if (_twinStartupApplied || DataContext is not MainViewModel model) return;
        _twinStartupApplied = true;
        if (model.StartupDiagnostics is { CanStart: true, TwinSchematicFollow: true } &&
            string.Equals(model.StartupDiagnostics.RuntimeMode, "physical", StringComparison.OrdinalIgnoreCase) &&
            model.ConnectionSource == RuntimeConnectionSource.PhysicalDevice)
            DigitalTwinView.EnableSchematicFollowing(true);
    }

    private void EnsureInitialTabSelection()
    {
        if (MainTabs.Items.Count == 0)
        {
            return;
        }

        if (MainTabs.SelectedItem is null)
        {
            MainTabs.SelectedIndex = 0;
        }

        if (MainTabs.SelectedItem is TabItem selectedTab)
        {
            SynchronizeNavigationSelection(selectedTab);
        }
    }

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tabName })
        {
            return;
        }

        if (_accessUser is not null && !AccessUiPolicy.CanNavigate(_accessUser.Role, tabName))
        {
            MessageBox.Show(this, AccessUiPolicy.Summary(_accessUser.Role), "当前账号权限", MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (PageFrame is not null) Dispatcher.BeginInvoke(new Action(ApplyPageAccess), System.Windows.Threading.DispatcherPriority.Loaded);
        if (IsTwinFullScreen && MainTabs.SelectedItem != DigitalTwinTab) SetTwinFullScreen(false);
        if (MainTabs.SelectedItem is not TabItem selectedTab)
        {
            return;
        }

        SynchronizeNavigationSelection(selectedTab);
    }

    private void SynchronizeNavigationSelection(TabItem selectedTab)
    {
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
