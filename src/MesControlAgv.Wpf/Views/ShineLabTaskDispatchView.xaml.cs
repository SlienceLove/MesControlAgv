using System.Windows;
using System.Windows.Controls;

namespace MesControlAgv.Wpf.Views;

public partial class ShineLabTaskDispatchView : UserControl
{
    private bool? _isCompactLayout;

    public ShineLabTaskDispatchView()
    {
        InitializeComponent();
    }

    private void DispatchWorkspaceGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DispatchWorkspaceGrid.ActualWidth <= 0)
        {
            return;
        }

        var isCompact = DispatchWorkspaceGrid.ActualWidth < 900;
        if (_isCompactLayout == isCompact)
        {
            return;
        }

        _isCompactLayout = isCompact;
        if (isCompact)
        {
            DispatchWorkspaceGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            DispatchWorkspaceGrid.RowDefinitions[1].Height = GridLength.Auto;
            DispatchWorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            DispatchWorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(0);
            Grid.SetRow(DispatchCommandPanel, 1);
            Grid.SetColumn(DispatchCommandPanel, 0);
            Grid.SetColumnSpan(DispatchCommandPanel, 3);
            DispatchCommandPanel.Margin = new Thickness(0, 12, 0, 0);
            DispatchCommandScrollViewer.MaxHeight = 360;
        }
        else
        {
            DispatchWorkspaceGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            DispatchWorkspaceGrid.RowDefinitions[1].Height = new GridLength(0);
            DispatchWorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(420);
            DispatchWorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(DispatchCommandPanel, 0);
            Grid.SetColumn(DispatchCommandPanel, 2);
            Grid.SetColumnSpan(DispatchCommandPanel, 1);
            DispatchCommandPanel.Margin = new Thickness(0);
            DispatchCommandScrollViewer.ClearValue(MaxHeightProperty);
        }
    }
}
