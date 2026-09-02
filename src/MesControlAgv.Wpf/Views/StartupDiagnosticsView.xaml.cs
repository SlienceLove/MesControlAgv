using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Wpf.ViewModels;
using Microsoft.Win32;

namespace MesControlAgv.Wpf.Views;

public partial class StartupDiagnosticsView : UserControl
{
    public StartupDiagnosticsView()
    {
        InitializeComponent();
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DiagnosticsCenterViewModel viewModel) return;
        var dialog = new SaveFileDialog
        {
            Filter = "JSON 诊断文件 (*.json)|*.json",
            AddExtension = true,
            DefaultExt = ".json",
            FileName = $"mes-offline-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Title = "导出脱敏离线诊断"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            viewModel.Export(dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"诊断导出失败：{exception.Message}",
                "导出脱敏诊断",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ImportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DiagnosticsCenterViewModel viewModel) return;
        var dialog = new OpenFileDialog
        {
            Filter = "JSON 诊断文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            Title = "读取离线诊断文件"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var result = viewModel.Import(dialog.FileName);
        if (!result.IsAccepted)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                result.Message,
                "读取离线诊断",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ClearAudit_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiagnosticsCenterViewModel viewModel) viewModel.Clear();
    }

    private void RecordDiffReview_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiagnosticsCenterViewModel viewModel)
        {
            viewModel.RecordDiffReview();
        }
    }

    private void ScanSnapshotLifecycle_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiagnosticsCenterViewModel viewModel)
        {
            viewModel.ScanSnapshotDirectory();
        }
    }

    private void MoveSnapshotCandidates_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiagnosticsCenterViewModel viewModel)
        {
            viewModel.MoveSnapshotCandidatesToQuarantine();
        }
    }

    private void ScanSnapshotQuarantine_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiagnosticsCenterViewModel viewModel)
        {
            viewModel.ScanSnapshotQuarantine();
        }
    }

    private void RestoreSnapshotCandidates_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiagnosticsCenterViewModel viewModel)
        {
            viewModel.RestoreSnapshotCandidates();
        }
    }
}
