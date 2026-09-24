using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Views;

public partial class ShineLabDeviceStatusView : UserControl
{
    public static readonly DependencyProperty ControlModelProperty = DependencyProperty.Register(nameof(ControlModel),
        typeof(ShineLabControlViewModel), typeof(ShineLabDeviceStatusView));
    public ShineLabControlViewModel? ControlModel
    {
        get => (ShineLabControlViewModel?)GetValue(ControlModelProperty);
        set => SetValue(ControlModelProperty, value);
    }

    public ShineLabDeviceStatusView()
    {
        InitializeComponent();
        Loaded += RefreshIonRecordsOnLoad;
        DeviceSummaryGrid.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && IsLoaded) RefreshIonRecordsOnLoad(this, new RoutedEventArgs());
        };
    }

    private async void RefreshIonRecordsOnLoad(object sender, RoutedEventArgs e)
    {
        if (DeviceSummaryGrid.IsVisible && ControlModel is { } model)
            await model.RefreshAsync(); // GET-only refresh; never dispatches device commands.
    }
}
