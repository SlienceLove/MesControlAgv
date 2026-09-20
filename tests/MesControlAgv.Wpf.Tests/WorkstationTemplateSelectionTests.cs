using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkstationTemplateSelectionTests
{
    private static SampleWorkstationTaskTemplate Template(string number = "SOURCE01") => new(number, "现有分液任务",
        [new("L01", "TIP01", 0, 0, "SRC01", 2, 3, "DST01", 4, 5, 125)]);

    [Fact]
    public void Reference_can_be_exported_but_cannot_be_previewed_until_codes_are_replaced() => Run(window =>
    {
        Click(window, "FillReference");
        var rows = Control<DataGrid>(window, "TransferGrid").Items.Cast<TransferEditorRow>().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, r => Assert.Equal("100", r.Volume));
        Assert.Equal("2", rows[1].SourceY);
        var task = new SampleWorkstationTaskTemplate(Control<TextBox>(window, "TaskNo").Text, "参考", rows.Select(r => r.ToContract()).ToArray());
        Assert.Equal(2, Assert.Single(WorkstationBatchWorkbook.Read(WorkstationBatchWorkbook.Write([task]))).Transfers.Count);
        Click(window, "PreviewSingle");
        Assert.Contains("替换", Control<TextBlock>(window, "Status").Text);
        Assert.False(Control<Button>(window, "ImportButton").IsEnabled);
        foreach (var r in rows) { r.LiquidCode = "L"; r.TipModule = "T"; r.SourceModule = "S"; r.TargetModule = "D"; }
        Click(window, "PreviewSingle");
        Assert.True(Control<Button>(window, "ImportButton").IsEnabled);
        Assert.Equal(1, Control<TabControl>(window, "WorkspaceTabs").SelectedIndex);
    });

    [Fact]
    public void Selection_pages_by_actual_count_and_copies_full_template_under_new_number()
    {
        var calls = new List<string>();
        Run(window =>
        {
            Assert.Empty(calls);
            Click(window, "LoadTasks");
            var selector = Control<ComboBox>(window, "SourceTasks");
            Assert.Single(selector.Items);
            Assert.Contains("现有分液任务", ((WorkstationSourceTask)selector.Items[0]).Label);
            Click(window, "MoreTasks");
            Assert.Contains("startNo=2", calls[1]);
            Assert.Equal(Visibility.Collapsed, Control<Button>(window, "MoreTasks").Visibility);
            selector.SelectedIndex = 0;
            var numberBefore = Control<TextBox>(window, "TaskNo").Text;
            Click(window, "CopySource");
            Assert.NotEqual(numberBefore, Control<TextBox>(window, "TaskNo").Text);
            Assert.NotEqual("SOURCE01", Control<TextBox>(window, "TaskNo").Text);
            Assert.Equal(Template().Transfers[0], Assert.Single(Control<DataGrid>(window, "TransferGrid").Items.Cast<TransferEditorRow>()).ToContract());
            Assert.Contains("来源任务保持不变", Control<TextBlock>(window, "Status").Text);
            Click(window, "PreviewSingle");
            Assert.True(Control<Button>(window, "ImportButton").IsEnabled);
        }, request =>
        {
            calls.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath.EndsWith("/template"))
                return Json(new SampleWorkstationTemplateResponse("WS-01", "SOURCE01.xlsx", [], "", Template(), DateTimeOffset.UtcNow));
            return Json(request.RequestUri.Query.Contains("startNo=1&")
                ? new[] { new SampleWorkstationTaskSummaryResponse(1, "SOURCE01", "现有分液任务", SampleWorkstationTaskState.Completed, "3", "", null) } : []);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_or_mismatched_copy_preserves_dirty_editor(bool mismatch) => Run(window =>
    {
        Control<TextBox>(window, "TaskName").Text = "未保存草稿";
        Control<ComboBox>(window, "SourceTasks").Text = "SOURCE01";
        Click(window, "CopySource");
        Assert.Equal("未保存草稿", Control<TextBox>(window, "TaskName").Text);
        Assert.Contains("读取模板失败", Control<TextBlock>(window, "Status").Text);
        Assert.False(Control<Button>(window, "ImportButton").IsEnabled);
    }, _ => mismatch ? Json(new SampleWorkstationTemplateResponse("OTHER-DEVICE", "SOURCE01.xlsx", [], "", Template(), DateTimeOffset.UtcNow))
        : new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("offline") });

    [Fact]
    public void Manual_number_copies_without_loading_list_and_invalid_repreview_clears_old_preview() => Run(window =>
    {
        Control<ComboBox>(window, "SourceTasks").Text = "SOURCE01";
        Click(window, "CopySource");
        Click(window, "PreviewSingle");
        Assert.True(Control<Button>(window, "ImportButton").IsEnabled);
        var row = Assert.Single(Control<DataGrid>(window, "TransferGrid").Items.Cast<TransferEditorRow>());
        row.Volume = "invalid";
        Click(window, "PreviewSingle");
        Assert.False(Control<Button>(window, "ImportButton").IsEnabled);
        Assert.Empty(Control<DataGrid>(window, "PreviewGrid").Items);
    }, request =>
    {
        Assert.EndsWith("/tasks/SOURCE01/template", request.RequestUri!.AbsolutePath);
        return Json(new SampleWorkstationTemplateResponse("WS-01", "SOURCE01.xlsx", [], "", Template(), DateTimeOffset.UtcNow));
    });

    [Fact]
    public void Repeated_page_stops_paging_and_failed_refresh_keeps_existing_list()
    {
        var count = 0;
        Run(window =>
        {
            Click(window, "LoadTasks");
            Click(window, "MoreTasks");
            Assert.Single(Control<ComboBox>(window, "SourceTasks").Items);
            Assert.Equal(Visibility.Collapsed, Control<Button>(window, "MoreTasks").Visibility);
            Assert.Contains("停止分页", Control<TextBlock>(window, "TaskListStatus").Text);
            Click(window, "LoadTasks");
            Assert.Single(Control<ComboBox>(window, "SourceTasks").Items);
            Assert.Contains("加载失败", Control<TextBlock>(window, "TaskListStatus").Text);
        }, _ => ++count < 3
            ? Json(new[] { new SampleWorkstationTaskSummaryResponse(1, "SOURCE01", "现有任务", SampleWorkstationTaskState.Completed, "3", "", null) })
            : new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("offline") });
    }

    [Fact]
    public void Offline_list_does_not_block_reference_or_discard_editor() => Run(window =>
    {
        Click(window, "LoadTasks");
        Assert.Contains("加载失败", Control<TextBlock>(window, "TaskListStatus").Text);
        Assert.True(Control<TabControl>(window, "WorkspaceTabs").IsEnabled);
        Click(window, "FillReference");
        Assert.Equal(2, Control<DataGrid>(window, "TransferGrid").Items.Count);
    }, _ => new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("offline") });

    [Theory]
    [InlineData(850, 560)]
    [InlineData(1200, 740)]
    public void Source_actions_and_preview_remain_in_bounds(double width, double height) => Run(window =>
    {
        window.Width = width; window.Height = height; window.Show(); window.UpdateLayout();
        foreach (var name in new[] { "CopySource", "FillReference", "PreviewSingle" })
        {
            var button = Control<Button>(window, name);
            var point = button.TransformToAncestor(window).Transform(new Point());
            Assert.True(point.X >= 0 && point.X + button.ActualWidth < window.ActualWidth, name);
            Assert.True(point.Y >= 0 && point.Y + button.ActualHeight < window.ActualHeight, name);
        }
        Assert.True(Control<DataGrid>(window, "TransferGrid").ActualHeight > 45);
    });

    private static T Control<T>(Window window, string name) where T : class => Assert.IsType<T>(window.FindName(name));
    private static void Click(Window window, string name)
    {
        Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!Control<TabControl>(window, "WorkspaceTabs").IsEnabled && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        Assert.True(Control<TabControl>(window, "WorkspaceTabs").IsEnabled);
    }
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static void Run(Action<WorkstationTaskWindow> action, Func<HttpRequestMessage, HttpResponseMessage>? response = null)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            WorkstationTaskWindow? window = null;
            try
            {
                using var http = new HttpClient(new Handler(response)) { BaseAddress = new Uri("http://localhost/") };
                window = new WorkstationTaskWindow(new MesClient(http), "WS-01", null, false);
                action(window);
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15))); Assert.Null(failure);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage>? response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method); // Template selection must never issue writes.
            Assert.NotNull(response);
            return Task.FromResult(response(request));
        }
    }
}
