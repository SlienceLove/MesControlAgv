using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using System.Windows.Media;
using System.Windows.Shapes;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Workflows;

namespace MesControlAgv.Wpf;

public partial class MainWindow : Window
{
    private WorkflowEditorViewModel? _workflowEditor;
    private INotifyCollectionChanged? _observedNodes;
    private WorkflowNode? _draggingNode;
    private FrameworkElement? _dragSource;
    private Point _dragOffset;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        DataContextChanged += MainWindow_DataContextChanged;
    }

    private async void ImportBatch_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var dialog = new OpenFileDialog
        {
            Filter = "任务文件 (*.xlsx;*.csv)|*.xlsx;*.csv|Excel 文件 (*.xlsx)|*.xlsx|CSV 文件 (*.csv)|*.csv",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) await viewModel.ImportBatchFileAsync(dialog.FileName);
    }
    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        AttachWorkflowEditor();
        RefreshWorkflowLinks();
    }

    private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachWorkflowEditor();
        RefreshWorkflowLinks();
    }

    private void AttachWorkflowEditor()
    {
        var editor = (DataContext as MainViewModel)?.WorkflowEditor;
        if (ReferenceEquals(_workflowEditor, editor)) return;
        if (_observedNodes is not null) _observedNodes.CollectionChanged -= WorkflowNodes_CollectionChanged;
        if (_workflowEditor is not null) _workflowEditor.PropertyChanged -= WorkflowEditor_PropertyChanged;

        _workflowEditor = editor;
        if (_workflowEditor is null) return;
        _workflowEditor.PropertyChanged += WorkflowEditor_PropertyChanged;
        AttachNodeCollection();
    }

    private void WorkflowEditor_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowEditorViewModel.SelectedWorkflow)) AttachNodeCollection();
        Dispatcher.BeginInvoke(RefreshWorkflowLinks);
    }

    private void AttachNodeCollection()
    {
        if (_observedNodes is not null) _observedNodes.CollectionChanged -= WorkflowNodes_CollectionChanged;
        _observedNodes = _workflowEditor?.SelectedWorkflow?.Nodes;
        if (_observedNodes is not null) _observedNodes.CollectionChanged += WorkflowNodes_CollectionChanged;
    }

    private void WorkflowNodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(RefreshWorkflowLinks);
    }

    private void WorkflowPalette_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || WorkflowPalette.SelectedItem is not WorkflowNodeTypeOption option) return;
        DragDrop.DoDragDrop(WorkflowPalette, option, DragDropEffects.Copy);
    }

    private void WorkflowCanvas_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(WorkflowNodeTypeOption)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void WorkflowCanvas_Drop(object sender, DragEventArgs e)
    {
        if (_workflowEditor is null || e.Data.GetData(typeof(WorkflowNodeTypeOption)) is not WorkflowNodeTypeOption option) return;
        var position = e.GetPosition(WorkflowNodes);
        _workflowEditor.AddNodeAt(option.Value, Math.Max(0, position.X - 85), Math.Max(0, position.Y - 40));
        RefreshWorkflowLinks();
        e.Handled = true;
    }

    private void WorkflowNode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not WorkflowNode node || _workflowEditor is null) return;
        _workflowEditor.SelectedNode = node;
        _draggingNode = node;
        _dragSource = element;
        _dragOffset = e.GetPosition(element);
        element.CaptureMouse();
        e.Handled = true;
    }

    private void WorkflowNode_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingNode is null || _dragSource is null || e.LeftButton != MouseButtonState.Pressed || !_dragSource.IsMouseCaptured) return;
        var position = e.GetPosition(WorkflowNodes);
        _draggingNode.X = Math.Max(0, Math.Min(1320, position.X - _dragOffset.X));
        _draggingNode.Y = Math.Max(0, Math.Min(638, position.Y - _dragOffset.Y));
        RefreshWorkflowLinks();
    }

    private void WorkflowNode_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragSource is not null && _dragSource.IsMouseCaptured) _dragSource.ReleaseMouseCapture();
        _dragSource = null;
        _draggingNode = null;
        RefreshWorkflowLinks();
        e.Handled = true;
    }

    private void RefreshWorkflowLinks()
    {
        if (WorkflowLinksCanvas is null) return;
        WorkflowLinksCanvas.Children.Clear();
        var nodes = _workflowEditor?.SelectedWorkflow?.Nodes.OrderBy(node => node.Order).ToList();
        if (nodes is null || nodes.Count < 2) return;

        for (var index = 0; index < nodes.Count - 1; index++)
        {
            var source = nodes[index];
            var target = nodes[index + 1];
            var x1 = source.X + 170;
            var y1 = source.Y + 41;
            var x2 = target.X;
            var y2 = target.Y + 41;
            var line = new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = new SolidColorBrush(Color.FromRgb(115, 129, 148)),
                StrokeThickness = 2,
                StrokeDashArray = x2 < x1 ? new DoubleCollection { 3, 3 } : null
            };
            WorkflowLinksCanvas.Children.Add(line);
            var arrow = new Polygon
            {
                Points = new PointCollection { new(0, 0), new(-10, -5), new(-10, 5) },
                Fill = new SolidColorBrush(Color.FromRgb(115, 129, 148))
            };
            Canvas.SetLeft(arrow, x2);
            Canvas.SetTop(arrow, y2);
            WorkflowLinksCanvas.Children.Add(arrow);
        }
    }
}
