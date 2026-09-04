using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Experiments;

public partial class ExperimentSchedulingView : UserControl
{
    private bool? _isCompactLayout;
    private ExperimentSchedulingViewModel? _viewModel;

    public ExperimentSchedulingView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel();
        ApplyTimelineFocusLayout();
        if (_viewModel is not null)
            await _viewModel.EnsureLoadedAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachViewModel();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachViewModel();
        ApplyTimelineFocusLayout();
    }

    private void AttachViewModel()
    {
        var viewModel = DataContext as ExperimentSchedulingViewModel;
        if (ReferenceEquals(_viewModel, viewModel)) return;
        DetachViewModel();
        _viewModel = viewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void DetachViewModel()
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(ExperimentSchedulingViewModel.IsTimelineFocusMode)) return;
        Dispatcher.BeginInvoke(ApplyTimelineFocusLayout);
    }

    private void ApplyTimelineFocusLayout()
    {
        var isFocus = _viewModel?.IsTimelineFocusMode == true;
        SchedulingTaskPoolPane.Visibility = isFocus ? Visibility.Collapsed : Visibility.Visible;
        SchedulingDetailsGrid.Visibility = isFocus ? Visibility.Collapsed : Visibility.Visible;
        SchedulingDetailsRow.Height = isFocus
            ? new GridLength(0)
            : new GridLength(265);

        if (isFocus)
        {
            SchedulingTaskPoolColumn.Width = new GridLength(0);
            SchedulingTimelineColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            ApplyWorkspaceColumns();
        }
    }

    private void ApplyWorkspaceColumns()
    {
        if (_isCompactLayout == true)
        {
            SchedulingTaskPoolColumn.Width = new GridLength(1.1, GridUnitType.Star);
            SchedulingTimelineColumn.Width = new GridLength(1.9, GridUnitType.Star);
        }
        else
        {
            SchedulingTaskPoolColumn.Width = new GridLength(320);
            SchedulingTimelineColumn.Width = new GridLength(1, GridUnitType.Star);
        }
    }

    private void SchedulingWorkspaceGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SchedulingWorkspaceGrid.ActualWidth <= 0)
        {
            return;
        }

        // Keep the task pool and timeline usable on 125%-scaled 1280/1366px
        // displays; the timeline itself retains a horizontal viewport.
        var isCompact = SchedulingWorkspaceGrid.ActualWidth < 1400;
        if (_isCompactLayout == isCompact)
        {
            if (_viewModel?.IsTimelineFocusMode == true)
                ApplyTimelineFocusLayout();
            return;
        }

        _isCompactLayout = isCompact;
        ApplyTimelineFocusLayout();
    }
}
