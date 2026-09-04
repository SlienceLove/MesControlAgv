using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace MesControlAgv.Wpf.Tests;

public sealed class MainWindowNavigationTests
{
    [Fact]
    public void Grouped_navigation_switches_existing_tab_content()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow
                {
                    WindowState = WindowState.Normal,
                    Width = 1200,
                    Height = 760
                };
                window.Show();
                var tabs = Assert.IsType<TabControl>(window.FindName("MainTabs"));
                var agvTab = Assert.IsType<TabItem>(window.FindName("AgvCommunicationTab"));
                var sidebar = Assert.IsType<Border>(window.FindName("NavigationSidebar"));
                var groupHeaders = FindVisualChildren<Expander>(sidebar)
                    .Select(expander => expander.Header?.ToString())
                    .Where(header => header is not null)
                    .ToArray();
                var agvButton = FindVisualChildren<RadioButton>(sidebar)
                    .Single(button => string.Equals(button.Tag as string, "AgvCommunicationTab", StringComparison.Ordinal));

                Assert.Equal(4, groupHeaders.Length);
                Assert.Contains("运营总览", groupHeaders);
                Assert.Contains("任务与设备", groupHeaders);
                Assert.Contains("实验工作流", groupHeaders);
                Assert.Contains("系统与诊断", groupHeaders);

                agvButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                Assert.Same(agvTab, tabs.SelectedItem);
                Assert.Equal("AGV 通讯与调度", ((TabItem)tabs.SelectedItem).Header);

                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF navigation test did not complete.");
        Assert.Null(failure);
    }

    [Fact]
    public void Agv_control_panel_stacks_below_table_in_compact_window()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow
                {
                    WindowState = WindowState.Normal,
                    Width = 820,
                    Height = 480
                };
                window.Show();
                var tabs = Assert.IsType<TabControl>(window.FindName("MainTabs"));
                tabs.SelectedItem = Assert.IsType<TabItem>(window.FindName("AgvCommunicationTab"));
                window.UpdateLayout();

                var agvView = Assert.IsType<Views.AgvCommunicationView>(window.FindName("AgvCommunicationView"));
                var workspace = Assert.IsType<Grid>(agvView.FindName("AgvWorkspaceGrid"));
                var controlPanel = Assert.IsType<Border>(agvView.FindName("AgvControlPanel"));

                Assert.Equal(1, Grid.GetRow(controlPanel));
                Assert.Equal(0, Grid.GetColumn(controlPanel));
                Assert.Equal(2, Grid.GetColumnSpan(controlPanel));
                Assert.True(workspace.ActualWidth < 760);

                window.Width = 1400;
                window.Height = 860;
                window.UpdateLayout();
                Assert.Equal(0, Grid.GetRow(controlPanel));
                Assert.Equal(1, Grid.GetColumn(controlPanel));
                Assert.Equal(1, Grid.GetColumnSpan(controlPanel));
                Assert.True(workspace.ActualWidth >= 760);

                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF compact AGV layout test did not complete.");
        Assert.Null(failure);
    }

    [Fact]
    public void Task_monitor_keeps_details_panel_inside_compact_window()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow
                {
                    WindowState = WindowState.Normal,
                    Width = 820,
                    Height = 480
                };
                window.Show();
                var tabs = Assert.IsType<TabControl>(window.FindName("MainTabs"));
                tabs.SelectedIndex = 0;
                window.Measure(new Size(820, 480));
                window.Arrange(new Rect(0, 0, 820, 480));
                window.UpdateLayout();

                var taskView = Assert.IsType<Views.TaskMonitorView>(window.FindName("TaskMonitorView"));
                var taskGrid = Assert.IsType<DataGrid>(taskView.FindName("TaskGrid"));
                var workspace = Assert.IsType<Grid>(taskView.FindName("TaskWorkspaceGrid"));
                var detailsScroll = Assert.IsType<ScrollViewer>(taskView.FindName("TaskDetailsScrollViewer"));
                var detailsHost = Assert.IsType<Grid>(taskView.FindName("TaskDetailsHost"));
                var scrollPosition = detailsScroll.TranslatePoint(new Point(0, 0), window);
                var position = detailsHost.TranslatePoint(new Point(0, 0), window);

                Assert.True(taskGrid.ActualWidth > 0);
                Assert.True(detailsHost.ActualWidth > 0);
                Assert.Equal(1, Grid.GetRow(detailsScroll));
                Assert.Equal(0, Grid.GetColumn(detailsScroll));
                Assert.Equal(2, Grid.GetColumnSpan(detailsScroll));
                Assert.True(workspace.ActualWidth < 900);
                Assert.True(
                    position.X + detailsHost.ActualWidth <= window.ActualWidth + 0.5,
                    "紧凑窗口下任务详情面板不应被推到窗口可视区之外。");

                window.Width = 1400;
                window.Height = 860;
                window.UpdateLayout();
                Assert.Equal(0, Grid.GetRow(detailsScroll));
                Assert.Equal(1, Grid.GetColumn(detailsScroll));
                Assert.Equal(1, Grid.GetColumnSpan(detailsScroll));
                Assert.True(workspace.ActualWidth >= 900);

                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF compact task layout test did not complete.");
        Assert.Null(failure);
    }

    [Fact]
    public void Sequence_import_stacks_validation_details_in_compact_window()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow
                {
                    WindowState = WindowState.Normal,
                    Width = 820,
                    Height = 480
                };
                window.Show();
                var tabs = Assert.IsType<TabControl>(window.FindName("MainTabs"));
                tabs.SelectedItem = Assert.IsType<TabItem>(window.FindName("ShineLabSequenceImportTab"));
                window.Measure(new Size(820, 480));
                window.Arrange(new Rect(0, 0, 820, 480));
                window.UpdateLayout();

                var view = Assert.IsType<Views.ShineLabSequenceImportView>(window.FindName("ShineLabSequenceImportView"));
                var workspace = Assert.IsType<Grid>(view.FindName("SequenceWorkspaceGrid"));
                var details = Assert.IsType<ScrollViewer>(view.FindName("SequenceDetailsScrollViewer"));

                Assert.Equal(1, Grid.GetRow(details));
                Assert.Equal(0, Grid.GetColumn(details));
                Assert.Equal(2, Grid.GetColumnSpan(details));
                Assert.True(workspace.ActualWidth < 900);

                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF compact sequence import layout test did not complete.");
        Assert.Null(failure);
    }

    [Fact]
    public void Device_status_stacks_selected_device_details_in_compact_window()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow
                {
                    WindowState = WindowState.Normal,
                    Width = 820,
                    Height = 480
                };
                window.Show();
                var tabs = Assert.IsType<TabControl>(window.FindName("MainTabs"));
                tabs.SelectedItem = Assert.IsType<TabItem>(window.FindName("ShineLabDeviceStatusTab"));
                window.Measure(new Size(820, 480));
                window.Arrange(new Rect(0, 0, 820, 480));
                window.UpdateLayout();

                var view = Assert.Single(FindVisualChildren<Views.ShineLabDeviceStatusView>(window));
                var workspace = Assert.IsType<Grid>(view.FindName("DeviceSummaryGrid"));
                var details = Assert.IsType<ScrollViewer>(view.FindName("SelectedDeviceScrollViewer"));

                Assert.Equal(1, Grid.GetRow(details));
                Assert.Equal(0, Grid.GetColumn(details));
                Assert.Equal(2, Grid.GetColumnSpan(details));
                Assert.True(workspace.ActualWidth < 800);

                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF compact device status layout test did not complete.");
        Assert.Null(failure);
    }

    [Fact]
    public void Shine_lab_dispatch_stacks_command_panel_in_compact_window()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow
                {
                    WindowState = WindowState.Normal,
                    Width = 820,
                    Height = 480
                };
                window.Show();
                var tabs = Assert.IsType<TabControl>(window.FindName("MainTabs"));
                tabs.SelectedItem = Assert.IsType<TabItem>(window.FindName("ShineLabTaskDispatchTab"));
                window.Measure(new Size(820, 480));
                window.Arrange(new Rect(0, 0, 820, 480));
                window.UpdateLayout();

                var view = Assert.Single(FindVisualChildren<Views.ShineLabTaskDispatchView>(window));
                var workspace = Assert.IsType<Grid>(view.FindName("DispatchWorkspaceGrid"));
                var commandPanel = Assert.IsType<Border>(view.FindName("DispatchCommandPanel"));

                Assert.Equal(1, Grid.GetRow(commandPanel));
                Assert.Equal(0, Grid.GetColumn(commandPanel));
                Assert.Equal(3, Grid.GetColumnSpan(commandPanel));
                Assert.True(workspace.ActualWidth < 900);

                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF compact ShineLab dispatch layout test did not complete.");
        Assert.Null(failure);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
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
