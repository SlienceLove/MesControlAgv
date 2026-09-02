using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using Microsoft.Win32;

namespace MesControlAgv.Wpf.Views;

public partial class MapDashboardView : UserControl
{
    private const double MapRouteFocusMaxScale = 0.8;

    private bool _mapPanning;
    private Point _mapPanStart;
    private DispatcherTimer? _mapAnimationTimer;
    private readonly MapAnimationCoordinator _mapAnimationCoordinator = new();
    private DateTimeOffset _mapAnimationLastTick;
    private MapViewModel? _observedMap;
    private bool _mapAutoFitted;

    public MapDashboardView()
    {
        InitializeComponent();
        Loaded += MapDashboardView_Loaded;
        Unloaded += MapDashboardView_Unloaded;
        DataContextChanged += MapDashboardView_DataContextChanged;
        IsVisibleChanged += MapDashboardView_IsVisibleChanged;
    }

    private void MapDashboardView_Loaded(object sender, RoutedEventArgs e)
    {
        AttachMapViewModel();
        StartMapAnimation();
        Dispatcher.BeginInvoke(TryAutoFitMap, DispatcherPriority.Loaded);
    }

    private void MapDashboardView_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachMapViewModel();
        if (_mapAnimationTimer is null) return;
        _mapAnimationTimer.Stop();
        _mapAnimationTimer = null;
    }

    private void MapDashboardView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        AttachMapViewModel();

    private void MapDashboardView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) Dispatcher.BeginInvoke(TryAutoFitMap, DispatcherPriority.Loaded);
    }

    private async void ImportMap_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var owner = Window.GetWindow(this);
        var dialog = new OpenFileDialog
        {
            Filter = "SMAP 地图文件 (*.smap)|*.smap|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            Title = "选择 SMAP 地图文件"
        };
        if (dialog.ShowDialog(owner) != true) return;

        var smapPath = dialog.FileName;
        var mappingDialog = new OpenFileDialog
        {
            Filter = "JSON 映射文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            Title = "选择站点映射文件（可选，点击取消跳过）"
        };
        var mappingPath = mappingDialog.ShowDialog(owner) == true ? mappingDialog.FileName : null;

        try
        {
            await viewModel.Readiness.LoadMapFromFileAsync(smapPath, mappingPath);
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!viewModel.Readiness.Map.UsingSmapLayout) return;
                FitMapToNavigationBounds(viewModel);
                _mapAutoFitted = true;
            }, DispatcherPriority.Loaded);
            MessageBox.Show(
                owner,
                $"地图已成功加载！\n文件：{System.IO.Path.GetFileName(smapPath)}\n布局验证：{viewModel.Readiness.MapLayoutVerificationStatus}",
                "导入 SMAP 地图",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                owner,
                $"地图加载失败：{exception.Message}",
                "导入 SMAP 地图",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void AttachMapViewModel()
    {
        var map = (DataContext as MainViewModel)?.Readiness.Map;
        if (ReferenceEquals(_observedMap, map)) return;
        DetachMapViewModel();
        _observedMap = map;
        _mapAutoFitted = false;
        if (_observedMap is not null) _observedMap.PropertyChanged += MapViewModel_PropertyChanged;
        Dispatcher.BeginInvoke(TryAutoFitMap, DispatcherPriority.Loaded);
    }

    private void DetachMapViewModel()
    {
        if (_observedMap is not null) _observedMap.PropertyChanged -= MapViewModel_PropertyChanged;
        _observedMap = null;
    }

    private void MapViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MapViewModel.NavigationBounds) or nameof(MapViewModel.UsingSmapLayout))) return;
        _mapAutoFitted = false;
        Dispatcher.BeginInvoke(TryAutoFitMap, DispatcherPriority.Loaded);
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
        if (DataContext is not MainViewModel viewModel) return;
        viewModel.Readiness.Viewport.Reset();
        _mapAutoFitted = true;
    }

    private void MapExportCurrent_Click(object sender, RoutedEventArgs e) => ExportMap(MapExportMode.CurrentViewport);
    private void MapExportFull_Click(object sender, RoutedEventArgs e) => ExportMap(MapExportMode.FullMap);

    private void ExportMap(MapExportMode mode)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var owner = Window.GetWindow(this);
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
            MessageBox.Show(owner, "地图尚未完成布局，无法导出 PNG。", "导出地图", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "PNG 图像 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = MapExportPlanner.CreatePngFileName(viewModel.Readiness.MapSnapshot?.ProfileMapName, mode)
        };
        if (dialog.ShowDialog(owner) != true) return;

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
                owner,
                $"已导出{scope} PNG：{plan.PixelWidth} x {plan.PixelHeight}\n{dialog.FileName}",
                "导出地图",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(owner, $"导出 PNG 失败：{exception.Message}", "导出地图", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (DataContext is MainViewModel resetViewModel) resetViewModel.Readiness.Viewport.Reset();
            _mapPanning = false;
            if (sender is UIElement resetElement) resetElement.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (DataContext is MainViewModel mapViewModel) mapViewModel.Readiness.Map.StationSelection.Clear();
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

    private void TryAutoFitMap()
    {
        if (_mapAutoFitted || !IsVisible || DataContext is not MainViewModel viewModel ||
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
        if (_mapAnimationTimer is not null) return;
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
        if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(1)) elapsed = TimeSpan.FromMilliseconds(50);

        var operationSignatures = viewModel.Readiness.FleetStatus
            .Where(item => item.ActiveTask is not null)
            .ToDictionary(
                item => item.Snapshot.AgvId,
                item => (string?)item.ActiveTask!.OperationId.ToString("D"),
                StringComparer.OrdinalIgnoreCase);
        _mapAnimationCoordinator.Sync(map.VisualAgvs, map.AgvPathSegments, operationSignatures);
        var poses = _mapAnimationCoordinator.Tick(elapsed);
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
}
