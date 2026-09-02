using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf.Tests;

public sealed class ShineLabSequenceImportViewBindingTests
{
    [Fact]
    public void Extracted_view_binds_nested_import_model_without_main_window_code_behind()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var import = new ShineLabSequenceImportViewModel();
                var window = new MainWindow
                {
                    WindowState = WindowState.Normal,
                    Width = 1000,
                    Height = 700,
                    DataContext = new ShineLabImportHost(import)
                };
                window.Show();

                var view = Assert.IsType<ShineLabSequenceImportView>(window.FindName("ShineLabSequenceImportView"));
                var textBox = Assert.IsType<TextBox>(view.FindName("TargetSequenceTextBox"));
                var submitButton = Assert.IsType<Button>(view.FindName("SubmitButton"));
                textBox.Text = "seq-offline-test";
                PumpDispatcher(window.Dispatcher);

                Assert.Equal("seq-offline-test", import.TargetSequence);
                Assert.Same(import.SubmitCommand, submitButton.Command);

                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "ShineLab view binding test did not complete.");
        Assert.Null(failure);
    }

    private static void PumpDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed record ShineLabImportHost(ShineLabSequenceImportViewModel ShineLabSequenceImport);
}
