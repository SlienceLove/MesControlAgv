using System.Windows;

namespace MesControlAgv.Wpf.Services;

public interface IWorkflowRunControlConfirmation
{
    bool Confirm(string title, string message);
}

public sealed class MessageBoxWorkflowRunControlConfirmation : IWorkflowRunControlConfirmation
{
    public static MessageBoxWorkflowRunControlConfirmation Instance { get; } = new();

    private MessageBoxWorkflowRunControlConfirmation()
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
