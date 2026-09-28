using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Views;

public partial class MaterialManagementView : UserControl
{
    public MaterialManagementView() => InitializeComponent();

    private async void ChooseImportFile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MaterialManagementViewModel viewModel) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "物料文件 (*.xlsx;*.csv)|*.xlsx;*.csv|Excel 文件 (*.xlsx)|*.xlsx|CSV 文件 (*.csv)|*.csv",
            CheckFileExists = true,
            Multiselect = false,
            Title = "选择物料导入文件"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            await viewModel.LoadImportFileAsync(dialog.FileName);
        }
    }

    private async void OpenDetectionAssessment_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MaterialManagementViewModel { SelectedSample: { } sample } viewModel)
        {
            MessageBox.Show(Window.GetWindow(this), "请先在样品列表中选中一条样品。", "检测判定",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var model = viewModel.DetectionStandards.CreateAssessment(sample);
        var dialog = new DetectionAssessmentDialog { Owner = Window.GetWindow(this), DataContext = model };
        await model.LoadAsync();
        dialog.ShowDialog();
    }

    private void FindMainViewModel(out MainViewModel? mainViewModel)
    {
        mainViewModel = null;
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: MainViewModel viewModel })
            {
                mainViewModel = viewModel;
                return;
            }

            current = current is FrameworkElement element
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(current);
        }
    }
}
