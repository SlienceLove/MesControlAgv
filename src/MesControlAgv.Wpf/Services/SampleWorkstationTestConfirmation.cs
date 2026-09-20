using System.Windows;

namespace MesControlAgv.Wpf.Services;

public interface ISampleWorkstationTestConfirmation
{
    bool Confirm(string title, string message);
}

public sealed class MessageBoxSampleWorkstationTestConfirmation : ISampleWorkstationTestConfirmation
{
    public static MessageBoxSampleWorkstationTestConfirmation Instance { get; } = new();

    private MessageBoxSampleWorkstationTestConfirmation()
    {
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
}
