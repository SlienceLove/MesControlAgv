using System.Windows;

namespace MesControlAgv.Wpf.Services;

public interface IShineLabOperationConfirmation
{
    bool Confirm(string title, string message);
}

public sealed class ShineLabOperationConfirmation : IShineLabOperationConfirmation
{
    public bool Confirm(string title, string message) => MessageBox.Show(message, title,
        MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}
