using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Views;

public partial class SampleManagementView : UserControl
{
    public SampleManagementView() => InitializeComponent();

    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "样品模板 (*.xlsx;*.csv)|*.xlsx;*.csv|Excel 文件 (*.xlsx)|*.xlsx|CSV 文件 (*.csv)|*.csv",
            CheckFileExists = true,
            Multiselect = false,
            Title = "选择样品导入模板"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (DataContext is SampleManagementViewModel viewModel)
        {
            await viewModel.ImportFileAsync(dialog.FileName);
            return;
        }

        var main = FindMainViewModel();
        if (main is not null) await main.ImportSampleFileAsync(dialog.FileName);
    }

    private MainViewModel? FindMainViewModel()
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: MainViewModel viewModel }) return viewModel;
            current = current is FrameworkElement element
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
