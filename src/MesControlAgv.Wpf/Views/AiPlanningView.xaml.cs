using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Views;

public partial class AiPlanningView : UserControl
{
    public AiPlanningView() => InitializeComponent();
    private async void OpenPlan_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AiPlanningViewModel { SavedPlan: { } plan } ||
            Window.GetWindow(this) is not MainWindow { DataContext: MainViewModel main } window) return;
        await main.ExperimentPlans.RefreshAsync(plan.PlanId, plan.Version);
        window.ShowExperimentPlans();
    }
}
