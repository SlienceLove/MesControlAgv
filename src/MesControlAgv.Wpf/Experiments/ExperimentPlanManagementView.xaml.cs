using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Experiments;

public partial class ExperimentPlanManagementView : UserControl
{
    private bool? _isCompactLayout;

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

    private void PlanWorkspaceGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PlanWorkspaceGrid.ActualWidth <= 0)
        {
            return;
        }

        var isCompact = PlanWorkspaceGrid.ActualWidth < 1100;
        if (_isCompactLayout == isCompact)
        {
            return;
        }

        _isCompactLayout = isCompact;
        if (isCompact)
        {
            // Keep all three panes visible on smaller screens. The detail pane
            // receives the largest share while the catalog/version lists remain
            // usable and can scroll their own rows.
            PlanCatalogColumn.Width = new GridLength(1.2, GridUnitType.Star);
            PlanVersionColumn.Width = new GridLength(0.8, GridUnitType.Star);
            PlanDetailsColumn.Width = new GridLength(2, GridUnitType.Star);
        }
        else
        {
            PlanCatalogColumn.Width = new GridLength(330);
            PlanVersionColumn.Width = new GridLength(165);
            PlanDetailsColumn.Width = new GridLength(1, GridUnitType.Star);
        }
    }
}
