using System.Windows;
using System.Windows.Controls;

namespace MesControlAgv.Wpf.Views;

public partial class AgvCommunicationView : UserControl
{
    public AgvCommunicationView()
    {
        InitializeComponent();
    }

    private void ResetAgvGridLayout_Click(object sender, RoutedEventArgs e) =>
        DataGridLayoutPersistence.Reset(AgvGrid);
}
