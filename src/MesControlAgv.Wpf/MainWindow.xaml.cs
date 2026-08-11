using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MesControlAgv.Domain.Map;
using MesControlAgv.Wpf.Services;
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
    private bool _mapPanning;
    private Point _mapPanStart;
    private DispatcherTimer? _mapAnimationTimer;
    private readonly MapAnimationCoordinator _mapAnimationCoordinator = new();
    private DateTimeOffset _mapAnimationLastTick;
    private MapViewModel? _observedMap;
    private bool _mapAutoFitted;

    private const double MapRouteFocusMaxScale = 2.1;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        DataContextChanged += MainWindow_DataContextChanged;
        Closed += MainWindow_Closed;
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
        AttachMapViewModel();
        RefreshWorkflowLinks();
        StartMapAnimation();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (_observedMap is not null)
        {
            _observedMap.PropertyChanged -= MapViewModel_PropertyChanged;
            _observedMap = null;
        }

        if (_mapAnimationTimer is null) return;
        _mapAnimationTimer.Stop();
        _mapAnimationTimer = null;
    }

    private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachWorkflowEditor();
        AttachMapViewModel();
        RefreshWorkflowLinks();
    }

    private void AttachMapViewModel()
    {
        var map = (DataContext as MainViewModel)?.Readiness.Map;
        if (ReferenceEquals(_observedMap, map)) return;
        if (_observedMap is not null) _observedMap.PropertyChanged -= MapViewModel_PropertyChanged;

        _observedMap = map;
        _mapAutoFitted = false;
        if (_observedMap is not null) _observedMap.PropertyChanged += MapViewModel_PropertyChanged;
        Dispatcher.BeginInvoke(TryAutoFitMap, DispatcherPriority.Loaded);
    }

    private void MapViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MapViewModel.NavigationBounds) or nameof(MapViewModel.UsingSmapLayout))) return;
        _mapAutoFitted = false;
        Dispatcher.BeginInvoke(TryAutoFitMap, DispatcherPriority.Loaded);
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

    private void MapCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var anchor = e.GetPosition(MapScrollViewer);
        viewModel.Readiness.Viewport.ZoomAt(anchor.X, anchor.Y, e.Delta);
        _mapAutoFitted = true;
        e.Handled = true;
    }

    private void MapZoomOut_Click(object sender, RoutedEventArgs e) => ZoomMapAtViewportCenter(-120);

    private void MapZoomIn_Click(object sender, RoutedEventArgs e) => ZoomMapAtViewportCenter(120);

    private void MapFit_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        viewModel.Readiness.Viewport.FitToViewport(
            MapScrollViewer.ActualWidth,
            MapScrollViewer.ActualHeight,
            viewModel.Readiness.Map.CanvasWidth,
            viewModel.Readiness.Map.CanvasHeight);
        _mapAutoFitted = true;
    }

    private void MapFitRoute_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        FitMapToNavigationBounds(viewModel);
        _mapAutoFitted = true;
    }

    private void MapReset_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.Readiness.Viewport.Reset();
            _mapAutoFitted = true;
        }
    }

    private void MapExportCurrent_Click(object sender, RoutedEventArgs e) =>
        ExportMap(MapExportMode.CurrentViewport);

    private void MapExportFull_Click(object sender, RoutedEventArgs e) =>
        ExportMap(MapExportMode.FullMap);

    private void ExportMap(MapExportMode mode)
    {
        if (DataContext is not MainViewModel viewModel) return;

        FrameworkElement source;
        double sourceWidth;
        double sourceHeight;
        if (mode == MapExportMode.CurrentViewport)
        {
            source = MapScrollViewer;
            sourceWidth = MapScrollViewer.ActualWidth;
            sourceHeight = MapScrollViewer.ActualHeight;
        }
        else
        {
            source = MapCanvas;
            sourceWidth = MapCanvas.ActualWidth;
            sourceHeight = MapCanvas.ActualHeight;
        }

        MapExportPlan plan;
        try
        {
            plan = MapExportPlanner.Create(mode, sourceWidth, sourceHeight);
        }
        catch (ArgumentOutOfRangeException)
        {
            MessageBox.Show(
                this,
                "地图尚未完成布局，无法导出 PNG。",
                "导出地图",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var mapName = viewModel.Readiness.MapSnapshot?.ProfileMapName;
        var dialog = new SaveFileDialog
        {
            Filter = "PNG 图像 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = MapExportPlanner.CreatePngFileName(mapName, mode)
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            source.UpdateLayout();
            new MapPngExportService().Export(
                source,
                new Rect(0, 0, plan.SourceWidth, plan.SourceHeight),
                dialog.FileName,
                plan.PixelWidth,
                plan.PixelHeight);
            var scope = mode == MapExportMode.CurrentViewport ? "当前视口" : "完整地图";
            MessageBox.Show(
                this,
                $"已导出{scope} PNG：{plan.PixelWidth} x {plan.PixelHeight}\n{dialog.FileName}",
                "导出地图",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"导出 PNG 失败：{exception.Message}",
                "导出地图",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ZoomMapAtViewportCenter(double delta)
    {
        if (DataContext is not MainViewModel viewModel) return;
        viewModel.Readiness.Viewport.ZoomAt(
            MapScrollViewer.ActualWidth / 2,
            MapScrollViewer.ActualHeight / 2,
            delta);
        _mapAutoFitted = true;
    }

    private void MapCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && TryGetMapNode(e.OriginalSource as DependencyObject, out var node))
        {
            viewModel.Readiness.Map.SelectStation(node.StationId);
            _mapPanning = false;
            if (sender is UIElement stationElement) stationElement.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (e.ClickCount > 1)
        {
            if (DataContext is MainViewModel resetViewModel)
            {
                resetViewModel.Readiness.Viewport.Reset();
            }

            _mapPanning = false;
            if (sender is UIElement resetElement) resetElement.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (DataContext is MainViewModel mapViewModel)
        {
            mapViewModel.Readiness.Map.StationSelection.Clear();
        }
        _mapPanning = true;
        _mapPanStart = e.GetPosition(MapScrollViewer);
        if (sender is UIElement captureElement) captureElement.CaptureMouse();
        e.Handled = true;
    }

    private void MapStation_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is FrameworkElement { DataContext: MapNodeViewModel node })
        {
            viewModel.Readiness.Map.SelectStation(node.StationId);
            e.Handled = true;
        }
    }

    private static bool TryGetMapNode(DependencyObject? source, out MapNodeViewModel node)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: MapNodeViewModel candidate })
            {
                node = candidate;
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        node = null!;
        return false;
    }

    private void MapCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_mapPanning || e.LeftButton != MouseButtonState.Pressed || DataContext is not MainViewModel viewModel) return;
        var current = e.GetPosition(MapScrollViewer);
        viewModel.Readiness.Viewport.Pan(current.X - _mapPanStart.X, current.Y - _mapPanStart.Y);
        _mapPanStart = current;
        _mapAutoFitted = true;
        e.Handled = true;
    }

    private void MapCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _mapPanning = false;
        if (sender is UIElement element) element.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, MainTabs) && MapTab is { IsSelected: true })
        {
            Dispatcher.BeginInvoke(TryAutoFitMap, DispatcherPriority.Loaded);
        }
    }

    private void TryAutoFitMap()
    {
        if (_mapAutoFitted || !MapTab.IsSelected || DataContext is not MainViewModel viewModel ||
            MapScrollViewer.ActualWidth <= 0 || MapScrollViewer.ActualHeight <= 0)
        {
            return;
        }

        FitMapToNavigationBounds(viewModel);
        _mapAutoFitted = true;
    }

    private void FitMapToNavigationBounds(MainViewModel viewModel) =>
        viewModel.Readiness.Viewport.FitToBounds(
            MapScrollViewer.ActualWidth,
            MapScrollViewer.ActualHeight,
            viewModel.Readiness.Map.NavigationBounds,
            maximumScale: MapRouteFocusMaxScale);

    private void StartMapAnimation()
    {
        _mapAnimationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _mapAnimationLastTick = DateTimeOffset.UtcNow;
        _mapAnimationTimer.Tick += MapAnimationTimer_Tick;
        _mapAnimationTimer.Start();
    }

    private void MapAnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.Readiness.Map.UsingSmapLayout) return;
        var map = viewModel.Readiness.Map;
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _mapAnimationLastTick;
        _mapAnimationLastTick = now;
        if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(1))
        {
            elapsed = TimeSpan.FromMilliseconds(50);
        }

        var operationSignatures = viewModel.Readiness.FleetStatus
            .Where(item => item.ActiveTask is not null)
            .ToDictionary(
                item => item.Snapshot.AgvId,
                item => (string?)item.ActiveTask!.OperationId.ToString("D"),
                StringComparer.OrdinalIgnoreCase);
        var poses = _mapAnimationCoordinator.Sync(
            map.VisualAgvs,
            map.AgvPathSegments,
            operationSignatures);
        poses = _mapAnimationCoordinator.Tick(elapsed);
        for (var index = 0; index < map.VisualAgvs.Count; index++)
        {
            var overlay = map.VisualAgvs[index];
            if (!poses.TryGetValue(overlay.AgvId, out var pose)) continue;
            map.VisualAgvs[index] = overlay with
            {
                X = pose.X,
                Y = pose.Y,
                HeadingRadians = pose.HeadingRadians,
                IsAnimating = pose.IsAnimating
            };
        }
    }

    private void RefreshWorkflowLinks()
    {
        if (WorkflowLinksCanvas is null) return;
        WorkflowLinksCanvas.Children.Clear();
        var nodes = _workflowEditor?.SelectedWorkflow?.Nodes.OrderBy(node => node.Order).ToList();
        if (nodes is null || nodes.Count < 2) return;

        var nodeById = nodes.ToDictionary(node => node.Id);
        var links = nodes.Any(node => node.NextNodeIds.Count > 0)
            ? nodes.SelectMany(source => source.NextNodeIds
                .Where(nodeById.ContainsKey)
                .Select(targetId => (Source: source, Target: nodeById[targetId])))
            : nodes.Zip(nodes.Skip(1), (source, target) => (Source: source, Target: target));

        foreach (var (source, target) in links)
        {
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

public sealed class MapBezierGeometryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MapEdgeViewModel edge)
        {
            return CreateGeometry(
                edge.X1,
                edge.Y1,
                edge.X2,
                edge.Y2,
                edge.Control1X,
                edge.Control1Y,
                edge.Control2X,
                edge.Control2Y);
        }

        if (value is MapPathSegmentViewModel segment)
        {
            return CreateGeometry(
                segment.X1,
                segment.Y1,
                segment.X2,
                segment.Y2,
                segment.Control1X,
                segment.Control1Y,
                segment.Control2X,
                segment.Control2Y);
        }

        return Geometry.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static PathGeometry CreateGeometry(
        double x1,
        double y1,
        double x2,
        double y2,
        double control1X,
        double control1Y,
        double control2X,
        double control2Y)
    {
        var figure = new PathFigure { StartPoint = new Point(x1, y1), IsClosed = false, IsFilled = false };
        if (double.IsNaN(control1X) || double.IsNaN(control1Y) ||
            double.IsNaN(control2X) || double.IsNaN(control2Y))
        {
            figure.Segments.Add(new LineSegment(new Point(x2, y2), isStroked: true));
        }
        else
        {
            figure.Segments.Add(new BezierSegment(
                new Point(control1X, control1Y),
                new Point(control2X, control2Y),
                new Point(x2, y2),
                isStroked: true));
        }

        return new PathGeometry([figure]);
    }
}
