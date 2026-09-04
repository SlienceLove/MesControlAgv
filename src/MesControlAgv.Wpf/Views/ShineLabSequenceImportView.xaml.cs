using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Views;

public partial class ShineLabSequenceImportView : UserControl
{
    private bool? _isCompactLayout;

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

    private void SequenceWorkspaceGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SequenceWorkspaceGrid.ActualWidth <= 0)
        {
            return;
        }

        var isCompact = SequenceWorkspaceGrid.ActualWidth < 900;
        if (_isCompactLayout == isCompact)
        {
            return;
        }

        _isCompactLayout = isCompact;
        if (isCompact)
        {
            SequenceWorkspaceGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            SequenceWorkspaceGrid.RowDefinitions[1].Height = GridLength.Auto;
            SequenceWorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            SequenceWorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetRow(SequenceDetailsScrollViewer, 1);
            Grid.SetColumn(SequenceDetailsScrollViewer, 0);
            Grid.SetColumnSpan(SequenceDetailsScrollViewer, 2);
            SequenceDetailsScrollViewer.Margin = new Thickness(0, 12, 0, 0);
            SequenceDetailsScrollViewer.MaxHeight = 300;
        }
        else
        {
            SequenceWorkspaceGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            SequenceWorkspaceGrid.RowDefinitions[1].Height = new GridLength(0);
            SequenceWorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            SequenceWorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(320);
            Grid.SetRow(SequenceDetailsScrollViewer, 0);
            Grid.SetColumn(SequenceDetailsScrollViewer, 1);
            Grid.SetColumnSpan(SequenceDetailsScrollViewer, 1);
            SequenceDetailsScrollViewer.Margin = new Thickness(12, 0, 0, 0);
            SequenceDetailsScrollViewer.ClearValue(MaxHeightProperty);
        }
    }
}
