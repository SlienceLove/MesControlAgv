using System.Windows;

namespace MesControlAgv.Wpf.Views;

public partial class CreateConsumableDialog : Window
{
    public CreateConsumableDialog() => InitializeComponent();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
