using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Workflows;
using Microsoft.Win32;

namespace MesControlAgv.Wpf.Views;

public partial class WorkflowManagementView : UserControl
{
    private WorkflowEditorViewModel? _workflowEditor;

    public WorkflowManagementView()
    {
        InitializeComponent();
        Loaded += WorkflowManagementView_Loaded;
        Unloaded += WorkflowManagementView_Unloaded;
        DataContextChanged += WorkflowManagementView_DataContextChanged;
    }

    private void WorkflowManagementView_Loaded(object sender, RoutedEventArgs e) => AttachWorkflowEditor();

    private void WorkflowManagementView_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachWorkflowEditor();
        WorkflowCanvasSurface.Detach();
    }

    private void WorkflowManagementView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        AttachWorkflowEditor();

    private void ImportWorkflowCompatibility_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new OpenFileDialog
        {
            Filter = "流程 JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            Title = "兼容导入流程文件"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var imported = viewModel.WorkflowEditor.ImportCompatibilityFile(dialog.FileName);
        var report = viewModel.WorkflowEditor.LastImportReport;
        if (report is null) return;
        var image = !imported || report.HasErrors
            ? MessageBoxImage.Error
            : report.HasWarnings
                ? MessageBoxImage.Warning
                : MessageBoxImage.Information;
        MessageBox.Show(
            Window.GetWindow(this),
            $"{report.Summary}\n\n{report.Details}",
            "流程转换报告",
            MessageBoxButton.OK,
            image);
    }

    private void AttachWorkflowEditor()
    {
        var editor = (DataContext as MainViewModel)?.WorkflowEditor;
        if (ReferenceEquals(_workflowEditor, editor)) return;
        DetachWorkflowEditor();
        _workflowEditor = editor;
        if (_workflowEditor is null)
        {
            WorkflowCanvasSurface.Detach();
            return;
        }
        _workflowEditor.PropertyChanged += WorkflowEditor_PropertyChanged;
        _workflowEditor.ValidationNavigationRequested += WorkflowEditor_ValidationNavigationRequested;
        AttachWorkflowCanvas();
    }

    private void DetachWorkflowEditor()
    {
        if (_workflowEditor is null) return;
        _workflowEditor.PropertyChanged -= WorkflowEditor_PropertyChanged;
        _workflowEditor.ValidationNavigationRequested -= WorkflowEditor_ValidationNavigationRequested;
        _workflowEditor = null;
    }

    private void WorkflowEditor_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowEditorViewModel.CanvasViewModel))
            Dispatcher.BeginInvoke(AttachWorkflowCanvas);
    }

    private void AttachWorkflowCanvas()
    {
        if (_workflowEditor?.CanvasViewModel is { } canvas)
        {
            WorkflowCanvasSurface.Attach(canvas);
            // New/local documents start with the neutral viewport (0,0,1),
            // which can place a station-trigger template below the initial
            // viewport. Fit once after layout so the flow is immediately
            // inspectable; preserve any viewport the operator has already saved.
            if (canvas.Document.Viewport is { X: 0, Y: 0, Zoom: 1 })
                Dispatcher.BeginInvoke(WorkflowCanvasSurface.FitToContent, DispatcherPriority.Loaded);
        }
        else
            WorkflowCanvasSurface.Detach();
    }

    private void WorkflowEditor_ValidationNavigationRequested(
        object? sender,
        WorkflowValidationIssueItemViewModel issue)
    {
        if (!ReferenceEquals(sender, _workflowEditor)) return;
        if (issue.EdgeId is { } edgeId)
        {
            WorkflowCanvasSurface.FocusEdge(edgeId);
            return;
        }
        if (issue.NodeId is { } nodeId) WorkflowCanvasSurface.FocusNode(nodeId);
    }

    private void WorkflowPalette_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            WorkflowPalette.SelectedItem is not WorkflowNodeTypeOption { IsAvailable: true } option) return;
        DragDrop.DoDragDrop(WorkflowPalette, option, DragDropEffects.Copy);
    }

    private void WorkflowCanvas_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(WorkflowNodeTypeOption)) is WorkflowNodeTypeOption { IsAvailable: true }
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void WorkflowCanvas_Drop(object sender, DragEventArgs e)
    {
        if (_workflowEditor is null || e.Data.GetData(typeof(WorkflowNodeTypeOption)) is not WorkflowNodeTypeOption option) return;
        var position = WorkflowCanvasSurface.GetGraphLocation(e);
        _workflowEditor.AddNodeAt(option.NodeTypeId, Math.Max(0, position.X - 100), Math.Max(0, position.Y - 60));
        WorkflowCanvasSurface.Focus();
        e.Handled = true;
    }

    private void WorkflowCanvas_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_workflowEditor?.CanvasViewModel?.HandleKey(e.Key, Keyboard.Modifiers) == true)
            e.Handled = true;
    }

    private void WorkflowCanvas_FitToContent(object sender, RoutedEventArgs e) => WorkflowCanvasSurface.FitToContent();
}
