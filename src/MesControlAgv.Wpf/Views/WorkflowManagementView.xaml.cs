using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MesControlAgv.Wpf;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Workflows;
using Microsoft.Win32;

namespace MesControlAgv.Wpf.Views;

public partial class WorkflowManagementView : UserControl
{
    private WorkflowEditorViewModel? _workflowEditor;
    private WorkflowManagementFullscreenWindow? _fullscreenWindow;
    private bool? _isCompactLayout;

    public bool IsFullscreenHost { get; set; }

    public WorkflowManagementView()
    {
        InitializeComponent();
        Loaded += WorkflowManagementView_Loaded;
        Unloaded += WorkflowManagementView_Unloaded;
        DataContextChanged += WorkflowManagementView_DataContextChanged;
        SizeChanged += WorkflowManagementView_SizeChanged;
    }

    private void WorkflowManagementView_Loaded(object sender, RoutedEventArgs e)
    {
        WorkflowCanvasFullscreenButton.Visibility = IsFullscreenHost ? Visibility.Collapsed : Visibility.Visible;
        AttachWorkflowEditor();
        ApplyResponsiveLayout();
    }

    private void WorkflowManagementView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!IsFullscreenHost)
            CloseFullscreenWindow();
        DetachWorkflowEditor();
        WorkflowCanvasSurface.Detach();
    }

    private void WorkflowManagementView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        AttachWorkflowEditor();

    private void WorkflowManagementView_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();

    private void ApplyResponsiveLayout()
    {
        if (Content is not Grid root)
        {
            return;
        }

        // The editor body is the direct Grid.Row=1 child of the page root. We
        // intentionally locate it structurally so this behavior coexists with
        // in-progress XAML additions (for example the physical batch fields).
        var workspace = root.Children
            .OfType<Grid>()
            .FirstOrDefault(child => Grid.GetRow(child) == 1 && child.ColumnDefinitions.Count == 3);
        if (workspace is null || workspace.ActualWidth <= 0)
        {
            return;
        }

        var isCompact = workspace.ActualWidth < 1100;
        if (_isCompactLayout == isCompact)
        {
            return;
        }

        _isCompactLayout = isCompact;
        var columns = workspace.ColumnDefinitions;
        foreach (var column in columns)
        {
            column.MinWidth = 0;
        }

        if (isCompact)
        {
            // Keep the palette and property inspector present on compact
            // screens while giving the canvas the largest flexible share.
            columns[0].Width = new GridLength(1.1, GridUnitType.Star);
            columns[1].Width = new GridLength(2.4, GridUnitType.Star);
            columns[2].Width = new GridLength(1.6, GridUnitType.Star);
        }
        else
        {
            columns[0].Width = new GridLength(190);
            columns[1].Width = new GridLength(1, GridUnitType.Star);
            columns[2].Width = new GridLength(360);
        }
    }

    private void WorkflowCanvasFullscreen_Checked(object sender, RoutedEventArgs e) =>
        OpenFullscreenWindow();

    private void WorkflowValidate_Click(object sender, RoutedEventArgs e) => WorkflowValidationPane.Visibility = Visibility.Visible;
    private void CloseValidation_Click(object sender, RoutedEventArgs e) => WorkflowValidationPane.Visibility = Visibility.Collapsed;

    private void WorkflowCanvasFullscreen_Unchecked(object sender, RoutedEventArgs e) =>
        CloseFullscreenWindow();

    private void OpenFullscreenWindow()
    {
        if (IsFullscreenHost)
            return;
        if (_fullscreenWindow is { IsVisible: true })
        {
            _fullscreenWindow.Activate();
            return;
        }

        var owner = Window.GetWindow(this);
        if (owner is null || DataContext is null)
        {
            WorkflowCanvasFullscreenButton.IsChecked = false;
            return;
        }

        WorkflowCanvasFullscreenButton.IsEnabled = false;
        try
        {
            _fullscreenWindow = new WorkflowManagementFullscreenWindow
            {
                DataContext = DataContext
            };
            if (owner.IsVisible)
                _fullscreenWindow.Owner = owner;
            _fullscreenWindow.Closed += (_, _) =>
            {
                _fullscreenWindow = null;
                WorkflowCanvasFullscreenButton.IsEnabled = true;
                WorkflowCanvasFullscreenButton.IsChecked = false;
            };
            _fullscreenWindow.Show();
        }
        catch
        {
            _fullscreenWindow = null;
            WorkflowCanvasFullscreenButton.IsEnabled = true;
            WorkflowCanvasFullscreenButton.IsChecked = false;
            throw;
        }
    }

    private void CloseFullscreenWindow()
    {
        if (IsFullscreenHost || _fullscreenWindow is null)
            return;
        var window = _fullscreenWindow;
        _fullscreenWindow = null;
        if (window.IsVisible)
            window.Close();
    }

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
