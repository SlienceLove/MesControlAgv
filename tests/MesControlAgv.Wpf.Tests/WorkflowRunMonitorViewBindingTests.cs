using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.WorkflowCanvas;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkflowRunMonitorViewBindingTests
{
    [Fact]
    public async Task Read_only_monitor_view_attaches_the_runtime_canvas()
    {
        var fixture = WorkflowRunMonitorFixture.Create();
        var monitor = new WorkflowRunMonitorViewModel(new WorkflowRunMonitorClientStub(fixture));
        await monitor.LoadAsync(fixture.Run.ExecutionId);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new WorkflowRunMonitorView { DataContext = monitor };
                var window = new Window
                {
                    Width = 1280,
                    Height = 800,
                    Content = view,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None
                };

                window.Show();
                PumpDispatcher(window.Dispatcher);

                var surface = Assert.IsType<NodifyCanvasAdapter>(view.FindName("RunCanvasSurface"));
                Assert.Same(monitor.CanvasViewModel, surface.DataContext);
                Assert.Equal(WorkflowCanvasMode.Runtime, monitor.CanvasViewModel!.CanvasMode);
                Assert.False(monitor.CanvasViewModel.IsEditing);
                Assert.True(surface.ActualWidth > 0);
                Assert.True(surface.ActualHeight > 0);
                var pause = Assert.IsType<Button>(view.FindName("PauseRunButton"));
                var resume = Assert.IsType<Button>(view.FindName("ResumeRunButton"));
                var cancel = Assert.IsType<Button>(view.FindName("CancelRunButton"));
                var resolveSucceeded = Assert.IsType<Button>(view.FindName("ResolveUnknownSucceededButton"));
                var resolveFailed = Assert.IsType<Button>(view.FindName("ResolveUnknownFailedButton"));
                Assert.Same(monitor.PauseCommand, pause.Command);
                Assert.Same(monitor.ResumeCommand, resume.Command);
                Assert.Same(monitor.CancelCommand, cancel.Command);
                Assert.Same(monitor.ResolveUnknownSucceededCommand, resolveSucceeded.Command);
                Assert.Same(monitor.ResolveUnknownFailedCommand, resolveFailed.Command);
                Assert.DoesNotContain("重试", resolveSucceeded.Content.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("重试", resolveFailed.Content.ToString(), StringComparison.Ordinal);
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

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Workflow run monitor binding test did not complete.");
        Assert.Null(failure);
    }

    private static void PumpDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
