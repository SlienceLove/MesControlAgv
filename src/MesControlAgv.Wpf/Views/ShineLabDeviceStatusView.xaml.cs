using System.Windows;
using System.Windows.Controls;

namespace MesControlAgv.Wpf.Views;

public partial class ShineLabDeviceStatusView : UserControl
{
    private bool? _isCompactLayout;

    public ShineLabDeviceStatusView()
    {
        InitializeComponent();
    }

    private void DeviceSummaryGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DeviceSummaryGrid.ActualWidth <= 0)
        {
            return;
        }

        var isCompact = DeviceSummaryGrid.ActualWidth < 800;
        if (_isCompactLayout == isCompact)
        {
            return;
        }

        _isCompactLayout = isCompact;
        if (isCompact)
        {
            DeviceSummaryGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            DeviceSummaryGrid.RowDefinitions[1].Height = GridLength.Auto;
            DeviceSummaryGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            DeviceSummaryGrid.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetRow(SelectedDeviceScrollViewer, 1);
            Grid.SetColumn(SelectedDeviceScrollViewer, 0);
            Grid.SetColumnSpan(SelectedDeviceScrollViewer, 2);
            SelectedDeviceScrollViewer.Margin = new Thickness(0, 12, 0, 0);
            SelectedDeviceScrollViewer.MaxHeight = 280;
        }
        else
        {
            DeviceSummaryGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            DeviceSummaryGrid.RowDefinitions[1].Height = new GridLength(0);
            DeviceSummaryGrid.ColumnDefinitions[0].Width = new GridLength(2, GridUnitType.Star);
            DeviceSummaryGrid.ColumnDefinitions[1].Width = new GridLength(3, GridUnitType.Star);
            Grid.SetRow(SelectedDeviceScrollViewer, 0);
            Grid.SetColumn(SelectedDeviceScrollViewer, 1);
            Grid.SetColumnSpan(SelectedDeviceScrollViewer, 1);
            SelectedDeviceScrollViewer.Margin = new Thickness(0);
            SelectedDeviceScrollViewer.ClearValue(MaxHeightProperty);
        }
    }
}
