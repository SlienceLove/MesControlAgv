using System.Windows;
using System.Windows.Controls;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Views;

public partial class ShineLabControlView : UserControl
{
    public ShineLabControlView() => InitializeComponent();
    public static readonly DependencyProperty DisplayModeProperty = DependencyProperty.Register(nameof(DisplayMode), typeof(string),
        typeof(ShineLabControlView), new PropertyMetadata("Tasks", (d, _) => ((ShineLabControlView)d).ApplyDisplayMode()));
    public string DisplayMode { get => (string)GetValue(DisplayModeProperty); set => SetValue(DisplayModeProperty, value); }
    private void ApplyDisplayMode()
    {
        if (DeviceTab is null) return;
        var device = DisplayMode == "Device";
        DeviceTab.Visibility = device ? Visibility.Visible : Visibility.Collapsed;
        ImportTab.Visibility = ResultsTab.Visibility = device ? Visibility.Collapsed : Visibility.Visible;
        if (device) SectionTabs.SelectedItem = DeviceTab;
        else if (SectionTabs.SelectedItem == DeviceTab) SectionTabs.SelectedItem = ImportTab;
        PageHeading.Text = device ? "通讯与托盘操作" : "离子色谱样品任务";
    }
    private void ImportLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = ImportLayout.ActualWidth < 1050;
        ImportLayout.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        ImportLayout.RowDefinitions[3].Height = compact ? new GridLength(1.3, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(ImportDetails, compact ? 3 : 1);
        Grid.SetColumn(ImportDetails, compact ? 0 : 1);
        ImportDetails.Margin = compact ? new Thickness(0, 10, 0, 0) : new Thickness(12, 0, 0, 0);
    }
    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyDisplayMode();
        if (DataContext is ShineLabControlViewModel model) await model.RefreshAsync();
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShineLabControlViewModel model || model.IsBusy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        { Filter = "样品任务文件 (*.csv;*.xlsx)|*.csv;*.xlsx", CheckFileExists = true, Multiselect = false, Title = "导入 DCSeq 单通道样品表" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await model.ImportAsync(dialog.FileName);
    }
}
