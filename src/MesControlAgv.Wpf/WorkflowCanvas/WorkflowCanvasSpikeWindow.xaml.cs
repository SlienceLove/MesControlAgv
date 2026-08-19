using System.Windows;
using System.Windows.Input;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.WorkflowCanvas;

/// <summary>Standalone host for the canvas assessment. It deliberately bypasses normal MES startup.</summary>
public partial class WorkflowCanvasSpikeWindow : Window
{
    private readonly WorkflowCanvasSpikeViewModel _viewModel = new();

    public WorkflowCanvasSpikeWindow()
    {
        InitializeComponent();
        FitWindowToWorkArea();
        DataContext = _viewModel;
        CanvasSurface.Attach(_viewModel);
    }

    private void FitToContent_Click(object sender, RoutedEventArgs e) => CanvasSurface.FitToContent();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.HandleKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    private void FitWindowToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height));
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
    }
}
