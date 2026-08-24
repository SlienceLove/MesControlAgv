using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Experiments;

public partial class ExperimentSchedulingView : UserControl
{
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
}
