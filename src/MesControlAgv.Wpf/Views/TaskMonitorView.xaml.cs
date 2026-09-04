using System.Windows;
using System.Windows.Controls;

namespace MesControlAgv.Wpf.Views;

public partial class TaskMonitorView : UserControl
{
    private bool? _isCompactLayout;

    public TaskMonitorView()
    {
        InitializeComponent();
    }

    private void ResetTaskGridLayout_Click(object sender, RoutedEventArgs e) =>
        DataGridLayoutPersistence.Reset(TaskGrid);

    private void TaskWorkspaceGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TaskWorkspaceGrid.ActualWidth <= 0)
        {
            return;
        }

        var isCompact = TaskWorkspaceGrid.ActualWidth < 900;
        if (_isCompactLayout == isCompact)
        {
            return;
        }

        _isCompactLayout = isCompact;
        if (isCompact)
        {
            TaskWorkspaceGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            TaskWorkspaceGrid.RowDefinitions[1].Height = GridLength.Auto;
            TaskWorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            TaskWorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetRow(TaskDetailsScrollViewer, 1);
            Grid.SetColumn(TaskDetailsScrollViewer, 0);
            Grid.SetColumnSpan(TaskDetailsScrollViewer, 2);
            TaskDetailsScrollViewer.Margin = new Thickness(0, 12, 0, 0);
            TaskDetailsScrollViewer.MaxHeight = 360;
        }
        else
        {
            TaskWorkspaceGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            TaskWorkspaceGrid.RowDefinitions[1].Height = new GridLength(0);
            TaskWorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            TaskWorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(310);
            Grid.SetRow(TaskDetailsScrollViewer, 0);
            Grid.SetColumn(TaskDetailsScrollViewer, 1);
            Grid.SetColumnSpan(TaskDetailsScrollViewer, 1);
            TaskDetailsScrollViewer.Margin = new Thickness(16, 0, 0, 0);
            TaskDetailsScrollViewer.ClearValue(MaxHeightProperty);
        }
    }
}
