using System.Windows;

namespace MesControlAgv.Wpf.Views;

public partial class CreateSampleDialog : Window
{
    public CreateSampleDialog() => InitializeComponent();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
