using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkstationCompactLayoutTests
{
    [Theory]
    [InlineData(820, 480, false, false)]
    [InlineData(820, 480, true, false)]
    [InlineData(1440, 900, false, false)]
    [InlineData(1440, 900, true, false)]
    [InlineData(820, 480, false, true)]
    [InlineData(820, 480, true, true)]
    public async Task Tasks_fill_available_space_and_scroll_without_moving_controls(int width, int height, bool testControls, bool stale)
    {
        var mes = new FakeMesClient([]) { SampleWorkstationSnapshot = Snapshot() };
        using var model = new ShineLabDeviceStatusViewModel(mes, sampleWorkstationTestControlEnabled: testControls)
        { SelectedInstrument = ShineLabDeviceStatusViewModel.SampleWorkstationInstrument };
        await model.RefreshAsync();
        if (stale)
        {
            // Retain real prior rows/alarm while all sections fail on refresh.
            mes.SampleWorkstationSnapshot = Snapshot() with { Error = new("WS", 2, "AbnormallyEnded", true, DateTimeOffset.UtcNow) };
            await model.RefreshAsync();
            mes.SampleWorkstationSnapshot = new(null, null, [], "status unavailable", "errors unavailable", "tasks unavailable");
            await model.RefreshAsync();
        }
        RunSta(() =>
        {
            var window = new MainWindow
            {
                Width = width, Height = height, WindowState = WindowState.Normal,
                DataContext = new { ShineLabDeviceStatus = model, ShineLabControl = new ShineLabControlViewModel(mes) }
            };
            try
            {
                window.Show();
                var tabs = Assert.IsType<TabControl>(window.FindName("MainTabs"));
                tabs.SelectedItem = window.FindName("ShineLabDeviceStatusTab");
                window.UpdateLayout();
                var view = Assert.Single(Children<ShineLabDeviceStatusView>(window));
                var workspace = Assert.IsType<Grid>(view.FindName("WorkstationWorkspace"));
                var table = Assert.IsType<DataGrid>(view.FindName("WorkstationTasksGrid"));
                var toolbar = Assert.IsType<WrapPanel>(view.FindName("WorkstationOnlineToolbar"));
                var testPanel = Assert.IsType<Border>(view.FindName("WorkstationTestPanel"));
                Assert.Equal(testControls, testPanel.IsVisible);
                Assert.True(table.ActualHeight >= (stale ? 60 : 80));
                Assert.True(((FrameworkElement)table.Parent).ActualHeight >= table.ActualHeight - 1,
                    $"Task grid must not be clipped: table={table.ActualHeight}, slot={((FrameworkElement)table.Parent).ActualHeight}, workspace={workspace.ActualHeight}, toolbar={toolbar.ActualHeight}, testPanel={testPanel.ActualHeight}.");
                Assert.True(table.ActualHeight > toolbar.ActualHeight);
                Assert.True(table.ActualWidth >= workspace.ActualWidth - 2);
                Assert.Equal(60, table.Items.Count);
                Assert.True(table.IsReadOnly);
                Assert.Empty(table.Columns.OfType<DataGridCheckBoxColumn>());
                var notice = Assert.IsType<TextBlock>(view.FindName("WorkstationNotice"));
                Assert.Equal(stale, notice.IsVisible);
                if (stale)
                {
                    Assert.StartsWith("设备有报警记录", notice.Text);
                    Assert.Contains("上次读取", notice.Text);
                    Assert.Equal(model.WorkstationOperatorNotice, notice.ToolTip);
                }
                Assert.False(Assert.IsType<TextBlock>(view.FindName("WorkstationEmptyText")).IsVisible);
                foreach (var label in new[] { "单任务模板", "批量导入任务", "刷新状态" })
                {
                    var button = Assert.Single(Children<Button>(view).Where(b => Equals(b.Content, label)));
                    Assert.True(button.IsVisible);
                    AssertInBounds(button, window);
                }
                AssertInBounds(table, window);
                if (testControls) AssertInBounds(Assert.IsType<Button>(view.FindName("WorkstationTestStartButton")), window);
                SaveEvidence(window, $"workstation-{width}-{testControls}-{stale}.png");
                var scroll = Children<ScrollViewer>(table).First();
                Assert.True(scroll.ScrollableHeight > 0);
                var before = toolbar.TransformToAncestor(window).Transform(new Point());
                table.ScrollIntoView(table.Items[table.Items.Count - 1]);
                window.UpdateLayout();
                Assert.True(scroll.VerticalOffset > 0);
                Assert.Equal(before, toolbar.TransformToAncestor(window).Transform(new Point()));
                Assert.Equal(0, mes.SampleWorkstationStartCallCount);
                Assert.Null(mes.LastShineLabCommand);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Normal_vendor_events_do_not_create_alarm_banners(int code)
    {
        var snapshot = Snapshot() with { Error = new("WS", code, "vendor event", true, DateTimeOffset.UtcNow) };
        var mes = new FakeMesClient([]) { SampleWorkstationSnapshot = snapshot };
        using var model = Model(mes);
        await model.RefreshAsync();
        Assert.Equal("在线", model.WorkstationOperatorOnline);
        Assert.False(model.HasWorkstationOperatorNotice);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task Abnormal_events_remain_visible_without_raw_diagnostics(int code)
    {
        var mes = new FakeMesClient([]) { SampleWorkstationSnapshot = Snapshot() with
        { Error = new("WS", code, "RAW-DIAGNOSTIC", true, DateTimeOffset.UtcNow) } };
        using var model = Model(mes);
        await model.RefreshAsync();
        Assert.Contains("报警", model.WorkstationOperatorNotice);
        Assert.DoesNotContain("RAW-DIAGNOSTIC", model.WorkstationOperatorNotice);
    }

    [Fact]
    public async Task Failed_refresh_marks_retained_rows_and_status_as_stale_and_clears_after_recovery()
    {
        var mes = new FakeMesClient([]) { SampleWorkstationSnapshot = Snapshot() };
        using var model = Model(mes);
        await model.RefreshAsync();
        var selected = model.WorkstationTasks[0]; model.SelectedWorkstationTask = selected;
        var changed = new List<string?>(); model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        mes.SampleWorkstationSnapshot = new(null, null, [], "HTTP-SECRET", "HTTP-SECRET", "HTTP-SECRET");
        await model.RefreshAsync();
        Assert.Equal(60, model.WorkstationTasks.Count);
        Assert.Equal(selected.TaskNo, model.SelectedWorkstationTask!.TaskNo);
        Assert.Equal("状态未更新", model.WorkstationOperatorOnline);
        Assert.Contains("上次读取", model.WorkstationOperatorNotice);
        Assert.DoesNotContain("HTTP-SECRET", model.WorkstationOperatorNotice);
        Assert.Contains(nameof(model.WorkstationOperatorNotice), changed);
        Assert.Contains(nameof(model.WorkstationOperatorOnline), changed);
        mes.SampleWorkstationSnapshot = Snapshot();
        await model.RefreshAsync();
        Assert.False(model.HasWorkstationOperatorNotice);
        Assert.Equal("在线", model.WorkstationOperatorOnline);
    }

    [Fact]
    public async Task Empty_offline_and_unknown_states_are_not_reported_as_online()
    {
        var mes = new FakeMesClient([]);
        using var model = Model(mes);
        Assert.True(model.HasWorkstationOperatorNotice);
        Assert.Equal("不可用", model.WorkstationOperatorOnline);
        foreach (var state in new[] { SampleWorkstationDeviceState.Offline, SampleWorkstationDeviceState.Unknown })
        {
            mes.SampleWorkstationSnapshot = Snapshot() with { Status = Snapshot().Status! with { State = state }, Tasks = [] };
            await model.RefreshAsync();
            Assert.NotEqual("在线", model.WorkstationOperatorOnline);
            Assert.Empty(model.WorkstationTasks);
        }
    }

    private static ShineLabDeviceStatusViewModel Model(FakeMesClient mes) => new(mes)
    { SelectedInstrument = ShineLabDeviceStatusViewModel.SampleWorkstationInstrument };
    private static SampleWorkstationDashboardSnapshot Snapshot() => new(
        new("SAMPLE-WORKSTATION-01", "WS", true, SampleWorkstationDeviceState.Idle, 0, DateTimeOffset.UtcNow),
        new("SAMPLE-WORKSTATION-01", 0, "TaskCompleted", true, DateTimeOffset.UtcNow),
        Enumerable.Range(1, 60).Select(i => new SampleWorkstationTaskSummaryResponse(i, $"TASK-{i:000}",
            $"分液任务 {i:000}", SampleWorkstationTaskState.Waiting, "0", "2026-09-24 10:00", "离线展示数据")).ToArray());
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
    private static void AssertInBounds(FrameworkElement element, Window window)
    {
        var point = element.TransformToAncestor(window).Transform(new Point());
        Assert.True(point.X >= 0 && point.Y >= 0);
        Assert.True(point.X + element.ActualWidth <= window.ActualWidth + 1);
        Assert.True(point.Y + element.ActualHeight <= window.ActualHeight + 1);
    }
    private static void SaveEvidence(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("MES_UI_EVIDENCE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); } catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Layout check did not complete.");
        Assert.Null(failure);
    }
}
