using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf.Tests;

public sealed class RuntimeConnectionSourceBindingTests
{
    [Fact]
    public void Agv_and_aubo_views_label_simulator_online_source_as_non_field_data()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var main = new MainViewModel(
                    new FakeMesClient([]),
                    new RecordingSimulatorControlClient());
                using var arm = new AuboArmControlViewModel(new FakeMesClient([]), "simulator");

                var agvView = new AgvCommunicationView { DataContext = main };
                var armView = new AuboArmControlView { DataContext = arm };
                using (Show(agvView))
                {
                    var badge = Assert.IsType<Border>(agvView.FindName("AgvConnectionSourceBadge"));
                    var label = Assert.IsType<TextBlock>(badge.Child);
                    Assert.Contains("本地模拟器", label.Text, StringComparison.Ordinal);
                    Assert.Contains("非现场设备", label.Text, StringComparison.Ordinal);
                }

                using (Show(armView))
                {
                    var badge = Assert.IsType<Border>(armView.FindName("AuboConnectionSourceBadge"));
                    var label = Assert.IsType<TextBlock>(badge.Child);
                    var status = Assert.IsType<TextBlock>(armView.FindName("AuboConnectionStatusText"));
                    Assert.Contains("本地模拟器", label.Text, StringComparison.Ordinal);
                    Assert.Contains("非现场设备", label.Text, StringComparison.Ordinal);
                    Assert.Contains("本地模拟器", status.Text, StringComparison.Ordinal);
                    Assert.DoesNotContain("物理设备在线", status.Text, StringComparison.Ordinal);
                }
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

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Runtime source binding test did not complete.");
        Assert.Null(failure);
    }

    private static IDisposable Show(FrameworkElement content)
    {
        var window = new Window
        {
            Width = 1100,
            Height = 720,
            Content = content,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        window.Show();
        PumpDispatcher(window.Dispatcher);
        return new DelegateDisposable(() =>
        {
            window.Close();
            PumpDispatcher(window.Dispatcher);
        });
    }

    private static void PumpDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
