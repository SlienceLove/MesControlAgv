using System.Windows;
using System.Windows.Controls;

namespace MesControlAgv.Wpf.Views;

public partial class TaskMonitorView : UserControl
{
    public TaskMonitorView()
    {
        InitializeComponent();
    }

    private void ResetTaskGridLayout_Click(object sender, RoutedEventArgs e) =>
        DataGridLayoutPersistence.Reset(TaskGrid);
}
