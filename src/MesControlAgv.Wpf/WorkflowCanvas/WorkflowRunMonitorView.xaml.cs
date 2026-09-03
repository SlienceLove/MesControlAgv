using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.WorkflowCanvas;

public partial class WorkflowRunMonitorView : UserControl
{
    private WorkflowRunMonitorViewModel? _viewModel;
    private WorkflowRunMonitorFullscreenWindow? _fullscreenWindow;

    /// <summary>
    /// The embedded monitor owns the refresh lifecycle. A fullscreen mirror
    /// shares the same ViewModel and canvas but must not stop the embedded
    /// monitor's polling when it closes.
    /// </summary>
    public bool OwnsAutoRefreshLifecycle { get; set; } = true;

    public WorkflowRunMonitorView()
    {
        InitializeComponent();
        Loaded += WorkflowRunMonitorView_Loaded;
        Unloaded += WorkflowRunMonitorView_Unloaded;
        IsVisibleChanged += WorkflowRunMonitorView_IsVisibleChanged;
        DataContextChanged += WorkflowRunMonitorView_DataContextChanged;
    }

    private void WorkflowRunMonitorView_Loaded(object sender, RoutedEventArgs e)
    {
        // The fullscreen mirror reuses this view. It must not expose a second
        // fullscreen action that could create nested windows.
        WorkflowRunFullscreenButton.Visibility = OwnsAutoRefreshLifecycle
            ? Visibility.Visible
            : Visibility.Collapsed;
        AttachViewModel();
    }

    private void WorkflowRunMonitorView_Unloaded(object sender, RoutedEventArgs e) => DetachViewModel();

    private void WorkflowRunMonitorView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!OwnsAutoRefreshLifecycle) return;
        if (_viewModel is null) return;
        if (IsVisible)
            _viewModel.StartAutoRefresh();
        else
            _viewModel.StopAutoRefresh();
    }

    private void WorkflowRunMonitorView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        if (IsLoaded) AttachViewModel();
    }

    private void AttachViewModel()
    {
        var viewModel = DataContext as WorkflowRunMonitorViewModel;
        if (ReferenceEquals(_viewModel, viewModel)) return;
        DetachViewModel();
        _viewModel = viewModel;
        if (_viewModel is null) return;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        AttachCanvas();
        if (OwnsAutoRefreshLifecycle)
            _viewModel.StartAutoRefresh();
    }

    private void DetachViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            if (OwnsAutoRefreshLifecycle)
                _viewModel.StopAutoRefresh();
        }
        _viewModel = null;
        RunCanvasSurface.Detach();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowRunMonitorViewModel.CanvasViewModel))
            Dispatcher.BeginInvoke(AttachCanvas, DispatcherPriority.Loaded);
    }

    private void AttachCanvas()
    {
        if (_viewModel?.CanvasViewModel is { } canvas)
            RunCanvasSurface.Attach(canvas);
        else
            RunCanvasSurface.Detach();
    }

    private void RunCanvas_FitToContent(object sender, RoutedEventArgs e) => RunCanvasSurface.FitToContent();

    private void OpenFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (!OwnsAutoRefreshLifecycle) return;
        if (DataContext is not WorkflowRunMonitorViewModel viewModel) return;
        if (_fullscreenWindow is not null)
        {
            if (_fullscreenWindow.IsVisible)
                _fullscreenWindow.Activate();
            return;
        }

        WorkflowRunFullscreenButton.IsEnabled = false;
        try
        {
            _fullscreenWindow = new WorkflowRunMonitorFullscreenWindow
            {
                Owner = Window.GetWindow(this),
                DataContext = viewModel
            };
            _fullscreenWindow.Closed += (_, _) =>
            {
                _fullscreenWindow = null;
                WorkflowRunFullscreenButton.IsEnabled = true;
            };
            _fullscreenWindow.Show();
        }
        catch
        {
            _fullscreenWindow = null;
            WorkflowRunFullscreenButton.IsEnabled = true;
            throw;
        }
    }
}
