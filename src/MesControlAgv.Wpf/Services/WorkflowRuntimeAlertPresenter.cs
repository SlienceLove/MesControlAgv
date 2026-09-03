using System.Windows;

namespace MesControlAgv.Wpf.Services;

/// <summary>
/// Presents a workflow safety notice to the operator. The default implementation
/// uses a modal warning only on a new warning transition; the monitor also keeps
/// the same message visible in its warning banner.
/// </summary>
public interface IWorkflowRuntimeAlertPresenter
{
    void ShowWarning(string title, string message);
}

public sealed class MessageBoxWorkflowRuntimeAlertPresenter : IWorkflowRuntimeAlertPresenter
{
    public static MessageBoxWorkflowRuntimeAlertPresenter Instance { get; } = new();

    private MessageBoxWorkflowRuntimeAlertPresenter()
    {
    }

    public void ShowWarning(string title, string message)
    {
        var application = Application.Current;
        if (application is null) return;

        void Show()
        {
            var owner = application.Windows
                              .OfType<Window>()
                              .FirstOrDefault(window => window.IsActive) ??
                        application.MainWindow;
            if (owner is not null)
            {
                MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        if (application.Dispatcher.CheckAccess())
            Show();
        else
            application.Dispatcher.Invoke(Show);
    }
}
