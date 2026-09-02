using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.WorkflowCanvas;
using MesControlAgv.Wpf.Workflows;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkflowMainWindowBindingTests
{
    [Fact]
    public void Main_window_binds_inspector_and_validation_edge_navigation()
    {
        using var fixture = new TempWorkflowFile();
        Exception? failure = null;
        Guid? expectedEdgeId = null;
        Guid? selectedEdgeId = null;
        var thread = new Thread(() =>
        {
            try
            {
                var editor = new WorkflowEditorViewModel(new WorkflowStore(fixture.Path));
                var document = Assert.IsType<WorkflowGraphDocument>(editor.SelectedGraphDocument);
                var edge = document.Edges.First();
                expectedEdgeId = edge.Id;
                editor.Validation.Load(
                    new WorkflowValidationResult
                    {
                        ValidatorVersion = "workflow-publication-v2",
                        Issues =
                        [
                            new WorkflowValidationIssue
                            {
                                Code = "EDGE_UI_BINDING",
                                Message = "Select the edge from the validation grid.",
                                NodeId = edge.SourceNodeId,
                                EdgeId = edge.Id
                            }
                        ]
                    },
                    document);
                var window = new MainWindow
                {
                    DataContext = new WorkflowBindingHost(editor)
                };
                window.ApplyTemplate();
                window.Measure(new Size(1420, 860));
                window.Arrange(new Rect(0, 0, 1420, 860));
                window.UpdateLayout();

                var workflowView = Assert.IsType<WorkflowManagementView>(window.FindName("WorkflowManagementView"));
                var surface = Assert.IsType<NodifyCanvasAdapter>(workflowView.FindName("WorkflowCanvasSurface"));
                var simulatorExecute = Assert.IsType<Button>(workflowView.FindName("WorkflowSimulatorExecuteButton"));
                surface.Attach(Assert.IsType<WorkflowCanvasSpikeViewModel>(editor.CanvasViewModel));
                PumpDispatcher(window.Dispatcher);
                Assert.Same(editor.ExecuteSimulatorCommand, simulatorExecute.Command);
                Assert.False(simulatorExecute.IsEnabled);
                var validationGrid = Assert.IsType<DataGrid>(workflowView.FindName("WorkflowValidationGrid"));
                validationGrid.SelectedItem = Assert.Single(
                    validationGrid.Items.OfType<WorkflowValidationIssueItemViewModel>());
                PumpDispatcher(window.Dispatcher);
                selectedEdgeId = editor.CanvasViewModel?.SelectedConnection?.Id;
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
        Assert.Equal(expectedEdgeId, selectedEdgeId);
    }

    private static void PumpDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
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
