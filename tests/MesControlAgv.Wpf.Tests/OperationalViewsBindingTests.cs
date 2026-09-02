using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Wpf.Controls;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf.Tests;

public sealed class OperationalViewsBindingTests
{
    [Fact]
    public async Task Extracted_task_and_kpi_views_keep_data_and_layout_bindings()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "MovingToPickup", 0, null);
        using var viewModel = new MainViewModel(new FakeMesClient([task]));
        await viewModel.RefreshAsync();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow
                {
                    WindowState = WindowState.Normal,
                    Width = 1200,
                    Height = 760,
                    DataContext = viewModel
                };
                window.Show();
                PumpDispatcher(window.Dispatcher);

                var taskView = Assert.IsType<TaskMonitorView>(window.FindName("TaskMonitorView"));
                var taskGrid = Assert.IsType<DataGrid>(taskView.FindName("TaskGrid"));
                var kpiView = Assert.IsType<KpiDashboardView>(window.FindName("KpiDashboardView"));
                var donut = Assert.IsType<KpiDonutChart>(kpiView.FindName("TaskDonutChart"));
                var trend = Assert.IsType<KpiTrendChart>(kpiView.FindName("TaskTrendChart"));
                var mapView = Assert.IsType<MapDashboardView>(window.FindName("MapDashboardView"));
                var mapScrollViewer = Assert.IsType<ScrollViewer>(mapView.FindName("MapScrollViewer"));
                var mapCanvas = Assert.IsType<Canvas>(mapView.FindName("MapCanvas"));

                Assert.Single(taskGrid.Items);
                Assert.Equal("TaskMonitor", DataGridLayoutPersistence.GetLayoutKey(taskGrid));
                Assert.Same(viewModel.Kpi.StatusSlices, donut.Slices);
                Assert.Same(viewModel.Kpi.TaskTrend, trend.Points);
                Assert.Same(viewModel, mapView.DataContext);
                Assert.NotNull(mapScrollViewer);
                Assert.NotNull(mapCanvas);

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

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Operational view binding test did not complete.");
        Assert.Null(failure);
    }

    private static void PumpDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
