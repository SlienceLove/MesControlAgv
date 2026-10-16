using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.Views;

namespace MesControlAgv.Wpf.Tests;

public sealed class WorkstationBatchReviewTests
{
    private static readonly WorkstationBatchReviewItem Row = new(Guid.NewGuid(), "WS-01", "20260924143809983251", "test",
        "Unknown", "Workbook identity mismatch", new('A', 64), new('B', 64), null);

    [Fact]
    public async Task Client_normalizes_operator_and_hash_text_before_checking_receipt()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var request = new WorkstationBatchReconcileRequest(Guid.NewGuid(), Row.TaskNo, Row.PayloadHash.ToLowerInvariant(),
            Row.JournalSha256.ToLowerInvariant(), " operator ", " checked transfers ", true, true);
        var audit = await new MesClient(http).ReconcileWorkstationBatchAsync("WS-01", Row.RequestId, request, default);
        Assert.Equal("operator", audit.Confirmation.Actor);
        Assert.Equal("checked transfers", audit.Confirmation.Reason);
        Assert.Equal(1, handler.Posts);
    }

    [Fact]
    public void Window_never_confirms_without_all_inputs_and_final_operator_approval() => Run((window, handler) =>
    {
        Assert.Equal(0, handler.Gets); Assert.Equal(0, handler.Posts);
        Wait(window.LoadReviewsAsync()); Select(window);
        Wait(window.ConfirmReviewAsync()); Assert.Equal(0, handler.Posts);
        Control<TextBox>(window, "ReviewActor").Text = "operator";
        Control<TextBox>(window, "ReviewReason").Text = "checked all transfer rows";
        Control<CheckBox>(window, "ReviewCreated").IsChecked = true;
        Wait(window.ConfirmReviewAsync()); Assert.Equal(0, handler.Posts);
        Control<CheckBox>(window, "ReviewTransfers").IsChecked = true;
        window.ConfirmBatchReview = _ => false;
        Assert.True(Control<Button>(window, "ConfirmReview").IsEnabled);
        Wait(window.ConfirmReviewAsync()); Assert.Equal(0, handler.Posts);
    });

    [Fact]
    public void Confirm_saves_receipt_shows_manual_outcome_and_prevents_duplicate_submission() => Run((window, handler) =>
    {
        Prepare(window); window.ConfirmBatchReview = _ => true;
        Wait(window.ConfirmReviewAsync());
        Assert.Equal(1, handler.Posts);
        var row = Assert.Single(Control<DataGrid>(window, "ReviewGrid").Items.Cast<WorkstationReviewRow>());
        Assert.Contains("人工确认", row.Label); Assert.Equal("Unknown", row.Item.OriginalStatus);
        Assert.NotNull(row.Item.Reconciliation);
        Assert.Single(Directory.GetFiles(window.ReviewReceiptDirectory, "*.json"));
        Assert.Contains("原回读异常保留", Control<TextBlock>(window, "Status").Text);
        Wait(window.ConfirmReviewAsync()); Assert.Equal(1, handler.Posts);
    });

    [Fact]
    public void Lost_response_keeps_confirmation_id_and_requires_readonly_refresh() => Run((window, handler) =>
    {
        Prepare(window); window.ConfirmBatchReview = _ => true; handler.Mode = "lost-response";
        Wait(window.ConfirmReviewAsync());
        Assert.Equal(1, handler.Posts);
        Assert.Contains("确认操作号", Control<TextBlock>(window, "Status").Text);
        Assert.False(Control<Button>(window, "ConfirmReview").IsEnabled);
        Wait(window.ConfirmReviewAsync()); Assert.Equal(1, handler.Posts);
        handler.Mode = ""; Wait(window.LoadReviewsAsync()); Select(window);
        Assert.NotNull(Assert.Single(Control<DataGrid>(window, "ReviewGrid").Items.Cast<WorkstationReviewRow>()).Item.Reconciliation);
        Assert.False(Control<Button>(window, "ConfirmReview").IsEnabled);
    });

    [Theory]
    [InlineData("wrong-device")]
    [InlineData("missing-api")]
    public void Bad_list_does_not_enable_manual_confirmation(string mode) => Run((window, handler) =>
    {
        handler.Mode = mode; Wait(window.LoadReviewsAsync());
        Assert.Empty(Control<DataGrid>(window, "ReviewGrid").Items);
        Assert.False(Control<Button>(window, "ConfirmReview").IsEnabled);
        Assert.Contains("读取失败", Control<TextBlock>(window, "Status").Text);
        Assert.Equal(0, handler.Posts);
    });

    [Theory]
    [InlineData("wrong-reply-id")]
    [InlineData("wrong-reply-outcome")]
    public void Mismatched_reply_cannot_be_shown_as_success(string mode) => Run((window, handler) =>
    {
        Prepare(window); window.ConfirmBatchReview = _ => true; handler.Mode = mode;
        Wait(window.ConfirmReviewAsync());
        Assert.Contains("结果未确认", Control<TextBlock>(window, "Status").Text);
        Assert.False(Control<Button>(window, "ConfirmReview").IsEnabled);
        Assert.Equal(1, handler.Posts);
    });

    [Fact]
    public void In_flight_confirmation_blocks_double_click_and_window_close() => Run((window, handler) =>
    {
        window.Show(); Prepare(window); window.ConfirmBatchReview = _ => true; handler.Mode = "pending";
        var pending = window.ConfirmReviewAsync();
        Assert.False(Control<TabControl>(window, "WorkspaceTabs").IsEnabled);
        Wait(window.ConfirmReviewAsync()); Assert.Equal(1, handler.Posts);
        window.Close(); Assert.True(window.IsVisible);
        handler.Release.SetResult(); Wait(pending);
        Assert.True(Control<TabControl>(window, "WorkspaceTabs").IsEnabled);
    });

    [Theory]
    [InlineData(850, 560)]
    [InlineData(1200, 740)]
    public void Review_actions_fit_the_supported_window_sizes(double width, double height) => Run((window, _) =>
    {
        window.Width = width; window.Height = height;
        Control<TabControl>(window, "WorkspaceTabs").SelectedIndex = 2;
        window.Show(); window.UpdateLayout();
        var button = Control<Button>(window, "ConfirmReview");
        var point = button.TransformToAncestor(window).Transform(new Point());
        Assert.True(point.Y >= 0 && point.Y + button.ActualHeight < window.ActualHeight);
        Assert.True(Control<DataGrid>(window, "ReviewGrid").ActualHeight > 0);
    });

    private static void Select(WorkstationTaskWindow window) => Control<DataGrid>(window, "ReviewGrid").SelectedIndex = 0;
    private static void Prepare(WorkstationTaskWindow window)
    {
        Wait(window.LoadReviewsAsync()); Select(window);
        Control<TextBox>(window, "ReviewActor").Text = "operator";
        Control<TextBox>(window, "ReviewReason").Text = "exact task and all transfer rows checked";
        Control<CheckBox>(window, "ReviewCreated").IsChecked = true;
        Control<CheckBox>(window, "ReviewTransfers").IsChecked = true;
    }
    private static T Control<T>(Window window, string name) where T : class => Assert.IsType<T>(window.FindName(name));
    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        Assert.True(task.IsCompleted); task.GetAwaiter().GetResult();
    }
    private static void Run(Action<WorkstationTaskWindow, Handler> action)
    {
        Exception? failure = null;
        var directory = Path.Combine(Path.GetTempPath(), "workstation-review-ui-" + Guid.NewGuid().ToString("N"));
        var thread = new Thread(() =>
        {
            WorkstationTaskWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                using var handler = new Handler();
                using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
                window = new(new MesClient(http), "WS-01", null, false) { ReviewReceiptDirectory = directory };
                action(window, handler);
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Assert.Null(failure);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Gets; public int Posts; public string Mode = "";
        private WorkstationBatchReconciliation? _saved;
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
            {
                Gets++;
                Assert.Equal("/api/workstations/WS-01/task-batches/reviews", request.RequestUri!.AbsolutePath);
                if (Mode == "missing-api") return new(HttpStatusCode.NotFound);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { Row with {
                    DeviceId = Mode == "wrong-device" ? "OTHER" : "WS-01", Reconciliation = _saved } }) };
            }
            Posts++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/api/workstations/WS-01/task-batches/{Row.RequestId:D}/reconcile-created", request.RequestUri!.AbsolutePath);
            var input = (await request.Content!.ReadFromJsonAsync<WorkstationBatchReconcileRequest>(token))!;
            Assert.True(input.ConfirmTaskCreated && input.ConfirmTransfersChecked);
            _saved = new(Row.RequestId, Row.DeviceId, Row.TaskNo, Row.TaskName, Row.OriginalStatus, Row.OriginalMessage,
                Row.PayloadHash, Row.JournalSha256, input,
                new(1, Row.TaskNo, Row.TaskName, SampleWorkstationTaskState.Waiting, "waiting", null, null, null, "operator", "2026-09-24", null),
                new("WS-01", "EQ", true, SampleWorkstationDeviceState.Idle, 0, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow, "CreatedWithReadbackException");
            if (Mode == "lost-response") throw new TaskCanceledException("response lost");
            if (Mode == "pending") await Release.Task.WaitAsync(token);
            var result = Mode switch { "wrong-reply-id" => _saved with { RequestId = Guid.NewGuid() },
                "wrong-reply-outcome" => _saved with { Outcome = "Imported" }, _ => _saved };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        }
    }
}
