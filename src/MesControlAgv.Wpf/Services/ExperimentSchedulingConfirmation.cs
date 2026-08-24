using System.Windows;

namespace MesControlAgv.Wpf.Services;

public interface IExperimentSchedulingConfirmation
{
    bool Confirm(string title, string message);
}

public sealed class MessageBoxExperimentSchedulingConfirmation : IExperimentSchedulingConfirmation
{
    public static MessageBoxExperimentSchedulingConfirmation Instance { get; } = new();

    private MessageBoxExperimentSchedulingConfirmation()
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
