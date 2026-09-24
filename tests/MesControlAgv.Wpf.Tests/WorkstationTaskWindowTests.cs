using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkstationTaskWindowTests
{
    [Theory]
    [InlineData(false, 1200, 740)]
    [InlineData(true, 850, 560)]
    public void Task_workspace_opens_without_network_or_import_and_keeps_actions_in_bounds(bool batch, double width, double height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            WorkstationTaskWindow? window = null;
            try
            {
                window = new WorkstationTaskWindow(new FakeMesClient([]), "WS-01", null, batch) { Width = width, Height = height };
                window.Show(); window.UpdateLayout();
                var tabs = Assert.IsType<TabControl>(window.FindName("WorkspaceTabs"));
                Assert.Equal(batch ? 1 : 0, tabs.SelectedIndex);
                var taskNo = Assert.IsType<TextBox>(window.FindName("TaskNo"));
                Assert.Matches("^[0-9]{20}$", taskNo.Text);
                var import = Assert.IsType<Button>(window.FindName("ImportButton"));
                Assert.False(import.IsEnabled);
                if (batch)
                {
                    Assert.True(import.ActualWidth > 0 && import.ActualHeight > 0);
                    var point = import.TransformToAncestor(window).Transform(new Point(0, 0));
                    Assert.True(point.Y >= 0 && point.Y + import.ActualHeight < window.ActualHeight);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15))); Assert.Null(failure);
    }

    [Fact]
    public void Editor_does_not_coerce_invalid_volume_to_a_default()
    {
        var row = new TransferEditorRow { LiquidCode = "L", TipModule = "T", SourceModule = "S", TargetModule = "D", Volume = "abc" };
        Assert.Throws<ArgumentException>(() => row.ToContract());
        row.Volume = "50";
        Assert.Equal(50, row.ToContract().VolumeMicroliters);
        Assert.Equal(0, row.ToContract().TipX);
    }
}
