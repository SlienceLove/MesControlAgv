using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.WorkflowCanvas;

public partial class WorkflowRunMonitorView : UserControl
{
    private WorkflowRunMonitorViewModel? _viewModel;

    public WorkflowRunMonitorView()
    {
        InitializeComponent();
        Loaded += WorkflowRunMonitorView_Loaded;
        Unloaded += WorkflowRunMonitorView_Unloaded;
        DataContextChanged += WorkflowRunMonitorView_DataContextChanged;
    }

    private void WorkflowRunMonitorView_Loaded(object sender, RoutedEventArgs e) => AttachViewModel();

    private void WorkflowRunMonitorView_Unloaded(object sender, RoutedEventArgs e) => DetachViewModel();

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
        _viewModel.StartAutoRefresh();
    }

    private void DetachViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
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
}
