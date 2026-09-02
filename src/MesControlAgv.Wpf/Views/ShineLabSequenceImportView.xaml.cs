using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Views;

public partial class ShineLabSequenceImportView : UserControl
{
    public ShineLabSequenceImportView()
    {
        InitializeComponent();
    }

    private void ImportShineLabSequence_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = FindMainViewModel();
        if (viewModel is null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "样品任务文件 (*.csv;*.xlsx)|*.csv;*.xlsx|CSV 文件 (*.csv)|*.csv|Excel 文件 (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false,
            Title = "选择离子色谱样品任务文件"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            viewModel.ShineLabSequenceImport.Load(dialog.FileName);
        }
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
