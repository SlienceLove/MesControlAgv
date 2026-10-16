using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Wpf.ViewModels;
using Microsoft.Win32;

namespace MesControlAgv.Wpf.Views;

public partial class LabReportView : UserControl
{
    public LabReportView() => InitializeComponent();
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LabReportViewModel { IsIdle: true } vm) return;
        var dialog = new OpenFileDialog { Filter = "Excel / CSV|*.xlsx;*.csv", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await vm.PreviewFileAsync(dialog.FileName);
    }
    private async void Template_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LabReportViewModel { IsIdle: true } vm) return;
        var dialog = new SaveFileDialog { Filter = "Excel 工作簿|*.xlsx", FileName = "实验结果导入模板.xlsx" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await vm.ExportTemplateAsync(dialog.FileName);
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LabReportViewModel { IsIdle: true } vm) return;
        var dialog = new SaveFileDialog { Filter = "Excel 完整报表|*.xlsx|CSV 测量明细|*.csv|HTML 可打印报表|*.html", FileName = "实验报表.xlsx" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await vm.ExportAsync(dialog.FileName);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => (DataContext as LabReportViewModel)?.Cancel();
}
