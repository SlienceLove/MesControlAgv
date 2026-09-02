using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf.Tests;

public sealed class BatchTaskImportViewBindingTests
{
    [Fact]
    public void Extracted_batch_view_keeps_commands_and_grid_layout_contract()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new BatchImportHost();
                var window = new MainWindow
                {
                    WindowState = WindowState.Normal,
                    Width = 1000,
                    Height = 700,
                    DataContext = host
                };
                window.Show();

                var view = Assert.IsType<BatchTaskImportView>(window.FindName("BatchTaskImportView"));
                var grid = Assert.IsType<DataGrid>(view.FindName("BatchTaskGrid"));
                var sortButton = Assert.IsType<Button>(view.FindName("SortButton"));
                var submitButton = Assert.IsType<Button>(view.FindName("SubmitButton"));
                var clearButton = Assert.IsType<Button>(view.FindName("ClearButton"));
                PumpDispatcher(window.Dispatcher);

                Assert.Same(host.SortBatchCommand, sortButton.Command);
                Assert.Same(host.SubmitBatchCommand, submitButton.Command);
                Assert.Same(host.ClearBatchCommand, clearButton.Command);
                Assert.Equal("BatchTaskImport", DataGridLayoutPersistence.GetLayoutKey(grid));
                Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetHorizontalScrollBarVisibility(grid));

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

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Batch view binding test did not complete.");
        Assert.Null(failure);
    }

    private static void PumpDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class BatchImportHost
    {
        public ObservableCollection<BatchTaskRowViewModel> BatchTasks { get; } = [];
        public ObservableCollection<string> BatchImportIssues { get; } = [];
        public string BatchStatus => "ready";
        public ICommand SortBatchCommand { get; } = new RoutedCommand();
        public ICommand SubmitBatchCommand { get; } = new RoutedCommand();
        public ICommand ClearBatchCommand { get; } = new RoutedCommand();
    }
}
