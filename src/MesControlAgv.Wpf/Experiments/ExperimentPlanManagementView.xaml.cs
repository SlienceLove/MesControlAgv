using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Experiments;

public partial class ExperimentPlanManagementView : UserControl
{
    public ExperimentPlanManagementView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ExperimentPlanManagementViewModel viewModel)
            await viewModel.EnsureLoadedAsync();
    }
}
