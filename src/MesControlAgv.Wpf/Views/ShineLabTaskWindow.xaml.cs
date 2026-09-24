using System.ComponentModel;
using System.Windows;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Views;

public partial class ShineLabTaskWindow : Window
{
    public ShineLabTaskWindow(ShineLabControlViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        InitializeComponent();
        DataContext = model; // Retain imported rows, prepared identity and replies across openings.
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (DataContext is ShineLabControlViewModel { IsBusy: true }) e.Cancel = true;
    }
}
