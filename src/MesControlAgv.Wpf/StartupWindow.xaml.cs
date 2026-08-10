using System.Windows;

namespace MesControlAgv.Wpf;

public partial class StartupWindow : Window
{
    public StartupWindow()
    {
        InitializeComponent();
    }

    public void SetStatus(string status)
    {
        StatusText.Text = status;
    }
}
