using System.Windows;
using System.Windows.Controls;

namespace MesControlAgv.Wpf.Views;

public partial class AgvCommunicationView : UserControl
{
    private bool? _isCompactLayout;

    public AgvCommunicationView()
    {
        InitializeComponent();
    }

    private void ResetAgvGridLayout_Click(object sender, RoutedEventArgs e)
    {
        try { DataGridLayoutPersistence.Reset(AgvGrid); }
        catch (Exception exception)
        {
            MessageBox.Show(Window.GetWindow(this), $"恢复列布局失败：{exception.Message}", "布局恢复", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AgvWorkspaceGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The table and the control panel can coexist comfortably above this
        // width. Below it, keeping the panel in a second column pushes the
        // table's desired width outside the viewport, so stack the panel below
        // the table while keeping its own vertical scroll area.
        if (AgvWorkspaceGrid.ActualWidth <= 0)
        {
            return;
        }

        var isCompact = AgvWorkspaceGrid.ActualWidth < 760;
        if (_isCompactLayout == isCompact)
        {
            return;
        }

        _isCompactLayout = isCompact;
        if (isCompact)
        {
            AgvWorkspaceGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            AgvWorkspaceGrid.RowDefinitions[1].Height = GridLength.Auto;
            AgvWorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            AgvWorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetRow(AgvControlPanel, 1);
            Grid.SetColumn(AgvControlPanel, 0);
            Grid.SetColumnSpan(AgvControlPanel, 2);
            AgvControlPanel.Margin = new Thickness(0, 12, 0, 0);
            AgvControlPanel.MaxHeight = 320;
        }
        else
        {
            AgvWorkspaceGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            AgvWorkspaceGrid.RowDefinitions[1].Height = new GridLength(0);
            AgvWorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            AgvWorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(260);
            Grid.SetRow(AgvControlPanel, 0);
            Grid.SetColumn(AgvControlPanel, 1);
            Grid.SetColumnSpan(AgvControlPanel, 1);
            AgvControlPanel.Margin = new Thickness(16, 0, 0, 0);
            AgvControlPanel.ClearValue(MaxHeightProperty);
        }
    }
}
