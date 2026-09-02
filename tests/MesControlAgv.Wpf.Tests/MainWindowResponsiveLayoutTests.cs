using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf.Tests;

public sealed class MainWindowResponsiveLayoutTests
{
    [Fact]
    public void Agv_page_keeps_control_panel_inside_compact_window()
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
                var agvTab = Assert.IsType<TabItem>(window.FindName("AgvCommunicationTab"));
                var mainTabs = Assert.IsType<TabControl>(window.FindName("MainTabs"));
                window.Show();
                mainTabs.SelectedItem = agvTab;
                window.Measure(new Size(820, 480));
                window.Arrange(new Rect(0, 0, 820, 480));
                window.UpdateLayout();

                var agvView = Assert.IsType<AgvCommunicationView>(window.FindName("AgvCommunicationView"));
                var agvGrid = Assert.IsType<DataGrid>(agvView.FindName("AgvGrid"));
                var controlPanel = Assert.IsType<Border>(agvView.FindName("AgvControlPanel"));
                var controlScrollViewer = Assert.IsType<ScrollViewer>(agvView.FindName("AgvControlScrollViewer"));
                var panelPosition = controlPanel.TranslatePoint(new Point(0, 0), window);

                Assert.Equal(820, window.MinWidth);
                Assert.Equal(480, window.MinHeight);
                Assert.True(double.IsNaN(agvGrid.Width), "AGV 表格应跟随可用宽度，而不是使用固定宽度。");
                Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetHorizontalScrollBarVisibility(agvGrid));
                Assert.Equal(ScrollBarVisibility.Auto, controlScrollViewer.VerticalScrollBarVisibility);
                Assert.True(controlPanel.ActualWidth > 0);
                Assert.True(
                    panelPosition.X + controlPanel.ActualWidth <= window.ActualWidth + 0.5,
                    "紧凑窗口下调度面板不应被推到窗口可视区之外。");

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

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF compact-layout test did not complete.");
        Assert.Null(failure);
    }
}
