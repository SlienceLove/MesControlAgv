using System.Windows;
using System.Windows.Threading;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkflowMainWindowBindingTests
{
    [Fact]
    public void Main_window_can_bind_read_only_workflow_inspector_metadata()
    {
        using var fixture = new TempWorkflowFile();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var editor = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
                var window = new MainWindow
                {
                    DataContext = new WorkflowBindingHost(editor)
                };
                window.ApplyTemplate();
                window.Measure(new Size(1420, 860));
                window.Arrange(new Rect(0, 0, 1420, 860));
                window.UpdateLayout();

                var frame = new DispatcherFrame();
                window.Dispatcher.BeginInvoke(
                    DispatcherPriority.ContextIdle,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
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

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF binding smoke test did not complete.");
        Assert.Null(failure);
    }

    private sealed record WorkflowBindingHost(WorkflowEditorViewModel WorkflowEditor);

    private sealed class TempWorkflowFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "MesControlAgv.WorkflowMainWindowBindingTests",
            Guid.NewGuid().ToString("N"));

        public string Path => System.IO.Path.Combine(_directory, "workflows.json");

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
