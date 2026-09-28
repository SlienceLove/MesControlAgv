using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Views;

public partial class DetectionStandardsView : UserControl
{
    private bool _loaded;

    public DetectionStandardsView() => InitializeComponent();

    private async void DetectionStandardsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || DataContext is not DetectionStandardsViewModel model) return;
        _loaded = true;
        await model.RefreshAsync();
    }
}
