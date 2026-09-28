using System.Windows;

namespace MesControlAgv.Wpf.Views;

public partial class DetectionAssessmentDialog : Window
{
    public DetectionAssessmentDialog() => InitializeComponent();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
