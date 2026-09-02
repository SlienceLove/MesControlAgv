using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Views;

public partial class BatchTaskImportView : UserControl
{
    public BatchTaskImportView()
    {
        InitializeComponent();
    }

    private async void ImportBatch_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = FindMainViewModel();
        if (viewModel is null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "任务文件 (*.xlsx;*.csv)|*.xlsx;*.csv|Excel 文件 (*.xlsx)|*.xlsx|CSV 文件 (*.csv)|*.csv",
            CheckFileExists = true,
            Multiselect = false,
            Title = "选择批量任务文件"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            await viewModel.ImportBatchFileAsync(dialog.FileName);
        }
    }

    private void ResetBatchGridLayout_Click(object sender, RoutedEventArgs e) =>
        DataGridLayoutPersistence.Reset(BatchTaskGrid);

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
