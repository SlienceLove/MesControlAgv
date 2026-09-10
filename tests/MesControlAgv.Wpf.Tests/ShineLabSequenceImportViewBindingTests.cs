using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Text;
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

    [Fact]
    public void Direct_import_grid_and_protocol_preview_bind_to_loaded_rows()
    {
        Exception? failure = null;
        var path = Path.Combine(Path.GetTempPath(), $"shinelab-direct-view-{Guid.NewGuid():N}.csv");
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(
            "sampleID,sampleName,sampleType,sampleLevel,processMethod,clearCalibration,cycleCount,injectionVolume,injectionVolumeUnit,position,mPos,channel,instrumentMethod,processingMethod,detectionMethod,blank,chromatographyMethod\n" +
            "LOCAL-STD-001,Standard,\u6807\u51c6\u6837,1,Anion,\u5426,1,25,uL,1,1,A,AS18-M01,IC-P01,Normal,\u5426,Anion\n"));
        var thread = new Thread(() =>
        {
            try
            {
                var import = new ShineLabSequenceImportViewModel();
                import.Load(path);
                var window = new MainWindow
                {
                    WindowState = WindowState.Normal,
                    Width = 1000,
                    Height = 700,
                    DataContext = new ShineLabImportHost(import)
                };
                window.Show();

                var view = Assert.IsType<ShineLabSequenceImportView>(window.FindName("ShineLabSequenceImportView"));
                var grid = Assert.IsType<DataGrid>(view.FindName("DirectTaskGrid"));
                var preview = Assert.IsType<TextBox>(view.FindName("DirectProtocolPreviewTextBox"));
                var preflight = Assert.IsType<Button>(view.FindName("SingleConfigPreflightButton"));
                PumpDispatcher(window.Dispatcher);

                Assert.Single(grid.Items);
                Assert.Contains("\"strMethod\": \"Config\"", preview.Text, StringComparison.Ordinal);
                Assert.True(preview.IsReadOnly);
                Assert.Same(import.SendSingleConfigPreflightCommand, preflight.Command);

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

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "ShineLab direct view binding test did not complete.");
        if (File.Exists(path)) File.Delete(path);
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
