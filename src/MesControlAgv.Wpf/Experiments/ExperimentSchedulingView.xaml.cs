using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Experiments;

public partial class ExperimentSchedulingView : UserControl
{
    private bool? _isCompactLayout;

    public ExperimentSchedulingView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ExperimentSchedulingViewModel viewModel)
            await viewModel.EnsureLoadedAsync();
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
            return;
        }

        _isCompactLayout = isCompact;
        if (isCompact)
        {
            // The timeline already has its own horizontal viewport. Give it
            // the larger share on compact screens while keeping the task pool
            // wide enough to identify and select a job.
            SchedulingTaskPoolColumn.Width = new GridLength(1.1, GridUnitType.Star);
            SchedulingTimelineColumn.Width = new GridLength(1.9, GridUnitType.Star);
        }
        else
        {
            SchedulingTaskPoolColumn.Width = new GridLength(320);
            SchedulingTimelineColumn.Width = new GridLength(1, GridUnitType.Star);
        }
    }
}
