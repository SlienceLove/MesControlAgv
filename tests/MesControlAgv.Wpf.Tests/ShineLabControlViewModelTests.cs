using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class ShineLabControlViewModelTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "DCSeq-SingleChannel-Wpf-Test.csv");

    [Fact]
    public async Task Refresh_import_and_prepare_do_not_send_device_commands_and_use_generated_preview()
    {
        var mes = new StubMes();
        var vm = new ShineLabControlViewModel(mes, new Confirm());
        await vm.RefreshAsync();
        Assert.True(vm.CanUseTray);
        await vm.ImportAsync(Fixture);
        Assert.Single(vm.ImportedSamples);
        Assert.Empty(vm.ImportIssues);
        Assert.Equal(0, mes.CreateCount);
        Assert.Equal(0, mes.ConfigCount);
        await vm.PrepareSelectedAsync();
        Assert.Equal(1, mes.CreateCount);
        Assert.Equal(0, mes.ConfigCount);
        Assert.Empty(mes.DeviceCommands);
        Assert.Contains("123456789", vm.ConfigPreview);
        Assert.Contains("STD-25", vm.PreparedSummary);
        Assert.DoesNotContain("channel", vm.ConfigPreview);
        Assert.True(vm.CanSendConfig);
        var id = mes.Task!.TaskUuid;
        await vm.PrepareSelectedAsync();
        Assert.Equal(id, mes.Task.TaskUuid); // Repeated preparation must not allocate another task identity.
    }

    [Fact]
    public async Task Config_requires_confirmation_and_is_only_sent_once_without_start()
    {
        var mes = new StubMes();
        var confirm = new Confirm { Approved = false };
        var vm = new ShineLabControlViewModel(mes, confirm);
        await vm.ImportAsync(Fixture);
        await vm.PrepareSelectedAsync();
        await vm.SendPreparedConfigAsync();
        Assert.Equal(0, mes.ConfigCount);
        confirm.Approved = true;
        await vm.SendPreparedConfigAsync();
        await vm.SendPreparedConfigAsync();
        Assert.Equal(1, mes.ConfigCount);
        Assert.Empty(mes.DeviceCommands);
        Assert.Contains("AQ", confirm.LastMessage);
        Assert.Contains("不等于", vm.LastReply);
    }

    [Fact]
    public async Task Unknown_config_outcome_is_not_automatically_retried()
    {
        var mes = new StubMes { ConfigThrows = true };
        var vm = new ShineLabControlViewModel(mes, new Confirm());
        await vm.ImportAsync(Fixture);
        await vm.PrepareSelectedAsync();
        await vm.SendPreparedConfigAsync();
        await vm.SendPreparedConfigAsync();
        Assert.Equal(1, mes.ConfigCount);
        Assert.False(vm.CanSendConfig);
        Assert.Contains("不要重复", vm.Message);
    }

    [Fact]
    public async Task Expired_cached_status_allows_config_click_to_refresh_and_confirm()
    {
        var mes = new StubMes { LastSeen = DateTimeOffset.UtcNow.AddMinutes(-2) };
        var confirm = new Confirm();
        var vm = new ShineLabControlViewModel(mes, confirm);
        await vm.ImportAsync(Fixture);
        await vm.PrepareSelectedAsync();
        Assert.True(vm.SendConfigCommand.CanExecute(null));
        mes.LastSeen = null; // Server has fresh evidence, but the page still holds the old snapshot.
        vm.SendConfigCommand.Execute(null);
        Assert.Equal(1, confirm.Count);
        Assert.Equal(1, mes.ConfigCount);
        Assert.False(vm.SendConfigCommand.CanExecute(null));
        Assert.Empty(mes.DeviceCommands);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("busy")]
    [InlineData("offline")]
    [InlineData("unavailable")]
    [InlineData("profile")]
    public async Task Config_refresh_blocks_unsafe_state_with_explicit_unsent_message(string condition)
    {
        var mes = new StubMes();
        var confirm = new Confirm();
        var vm = new ShineLabControlViewModel(mes, confirm);
        await vm.ImportAsync(Fixture);
        await vm.PrepareSelectedAsync();
        if (condition == "stale") mes.LastSeen = DateTimeOffset.UtcNow.AddMinutes(-2);
        if (condition == "busy") mes.Status = 1;
        if (condition == "offline") mes.Online = false;
        if (condition == "unavailable") mes.StatusThrows = true;
        if (condition == "profile") mes.Profile = "Rike";
        await vm.SendPreparedConfigAsync();
        Assert.Equal(0, confirm.Count);
        Assert.Equal(0, mes.ConfigCount);
        Assert.Contains("预检未通过，未发送", vm.Message);
        Assert.Empty(mes.DeviceCommands);
    }

    [Fact]
    public async Task Config_rechecks_after_confirmation_and_allows_retry_of_unsent_preflight()
    {
        var mes = new StubMes();
        var confirm = new Confirm { OnConfirm = () => mes.Status = 1 };
        var vm = new ShineLabControlViewModel(mes, confirm);
        await vm.ImportAsync(Fixture);
        await vm.PrepareSelectedAsync();
        await vm.SendPreparedConfigAsync();
        Assert.Equal(0, mes.ConfigCount);
        Assert.Contains("未发送", vm.Message);
        mes.Status = 0;
        confirm.OnConfirm = null;
        await vm.SendPreparedConfigAsync();
        Assert.Equal(1, mes.ConfigCount);
        Assert.Equal(2, confirm.Count);
    }

    [Theory]
    [InlineData("TrayPopup", true)]
    [InlineData("TrayReset", true)]
    [InlineData("TrayPopup", false)]
    [InlineData("TrayReset", false)]
    public async Task Tray_expired_cache_refreshes_but_stale_server_evidence_never_dispatches(string action, bool recovers)
    {
        var mes = new StubMes { LastSeen = DateTimeOffset.UtcNow.AddMinutes(-2) };
        var confirm = new Confirm();
        var vm = new ShineLabControlViewModel(mes, confirm);
        await vm.RefreshAsync();
        var command = action == "TrayPopup" ? vm.TrayPopupCommand : vm.TrayResetCommand;
        Assert.True(command.CanExecute(null));
        if (recovers) mes.LastSeen = null;
        command.Execute(null);
        Assert.Equal(recovers ? 1 : 0, confirm.Count);
        Assert.Equal(recovers ? 1 : 0, mes.DeviceCommands.Count);
        if (!recovers) Assert.Contains("预检未通过，未发送", vm.LastReply);
        Assert.Equal(0, mes.ConfigCount);
    }

    [Theory]
    [InlineData("TrayPopup")]
    [InlineData("TrayReset")]
    public async Task Tray_is_an_explicit_single_device_command_and_does_not_claim_position(string action)
    {
        var mes = new StubMes();
        var vm = new ShineLabControlViewModel(mes, new Confirm());
        await vm.RefreshAsync();
        await vm.SendTrayAsync(action);
        var sent = Assert.Single(mes.DeviceCommands);
        Assert.Equal(action, sent.StrAction);
        Assert.Equal("", sent.Param);
        Assert.Null(sent.Channel);
        Assert.Equal(0, mes.CreateCount);
        Assert.Equal(0, mes.ConfigCount);
        Assert.Contains("现场确认", vm.LastReply);
        Assert.Contains("wire-id", vm.LastReply);
    }

    [Fact]
    public async Task Device_becoming_busy_during_confirmation_blocks_dispatch()
    {
        var mes = new StubMes();
        var confirm = new Confirm { OnConfirm = () => mes.Status = 1 };
        var vm = new ShineLabControlViewModel(mes, confirm);
        await vm.RefreshAsync();
        await vm.SendTrayAsync("TrayPopup");
        Assert.Empty(mes.DeviceCommands);
        Assert.False(vm.CanUseTray);
    }

    [Theory]
    [InlineData(false, 0, "DcSeqSingleChannel")]
    [InlineData(true, 1, "DcSeqSingleChannel")]
    [InlineData(true, 99, "DcSeqSingleChannel")]
    [InlineData(true, 0, "Rike")]
    public async Task Controls_fail_closed_when_capability_or_state_is_not_ready(bool enabled, int status, string profile)
    {
        var mes = new StubMes { Enabled = enabled, Status = status, Profile = profile };
        var vm = new ShineLabControlViewModel(mes, new Confirm());
        await vm.RefreshAsync();
        Assert.False(vm.CanUseTray);
        await vm.SendTrayAsync("TrayPopup");
        Assert.Empty(mes.DeviceCommands);
    }

    [Fact]
    public async Task Results_show_wire_concentration_and_keep_unknown_distinct_from_results_received()
    {
        var mes = new StubMes();
        var vm = new ShineLabControlViewModel(mes, new Confirm());
        await vm.ImportAsync(Fixture);
        await vm.PrepareSelectedAsync();
        mes.ResultJson = """{"compounds":[{"name":"TEST_ION_01","retaintime":5.055,"area":4.422294563888683,"concent":1.3657493408737929,"concunit":"mg/L"}]}""";
        mes.Task = mes.Task! with { Status = "Unknown", LastError = "previous Config timeout" };
        await vm.RefreshAsync();
        Assert.Single(vm.TaskSamples);
        var result = Assert.Single(vm.Compounds);
        Assert.Equal("1.3657493408737929", result.Concentration);
        Assert.Equal("mg/L", result.Unit);
        Assert.Contains("Unknown", vm.TaskSummary);
        Assert.Contains("不等于正常结束", vm.TaskSummary);
    }

    [Fact]
    public void Single_channel_parser_rejects_silent_channel_cycle_and_type_changes()
    {
        var content = File.ReadAllText(Fixture);
        var parser = new ShineLabDirectSequenceParser();
        foreach (var csv in new[]
        {
            content.Replace("cycleCount", "channel,cycleCount").Replace("25,uL,1", "25,uL,A,1"),
            content.Replace("25,uL,1", "25,uL,2"),
            content.Replace("STD-25_WPF_TEST,0", "STD-25_WPF_TEST,2")
        })
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
            Assert.False(parser.Parse(stream, "test.csv", ShineLabPayloadProfile.DcSeqSingleChannel).CanBuildPreview);
        }
    }

    [Fact]
    public async Task Mes_client_uses_read_only_detail_preview_and_server_routes()
    {
        var paths = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            paths.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath.EndsWith("server/status")
                ? JsonContent.Create(new ShineLabServerStatusResponse(true, "127.0.0.1", 5500, 60, 10000, "DcSeqSingleChannel", true))
                : JsonContent.Create(new { });
        })) { BaseAddress = new Uri("http://localhost/") };
        var client = new MesClient(http);
        Assert.True((await client.GetShineLabServerStatusAsync(default)).EnableTrayCommands);
        await client.GetShineLabTaskConfigPreviewAsync("task-1", default);
        await client.GetShineLabTaskDetailAsync("task-1", default);
        Assert.Equal(new[] { "/api/shinelab/server/status", "/api/shinelab/tasks/task-1/config-preview", "/api/shinelab/tasks/task-1" }, paths);
    }

    [Fact]
    public void New_page_is_accessible_from_main_navigation_and_renders_without_device_writes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var mes = new StubMes();
                var vm = new ShineLabControlViewModel(mes, new Confirm());
                var window = new MainWindow { WindowState = WindowState.Normal, Width = 1200, Height = 800 };
                var tabs = Assert.IsType<TabControl>(window.FindName("MainTabs"));
                var tab = Assert.IsType<TabItem>(window.FindName("InstrumentTaskImportTab"));
                var import = Assert.IsType<Views.InstrumentTaskImportView>(tab.Content);
                import.DataContext = new { ShineLabControl = vm };
                window.Show(); tabs.SelectedItem = tab; window.UpdateLayout();
                Assert.True(import.ActualWidth > 0);
                Assert.True(import.ActualHeight > 0);
                var oldTab = Assert.IsType<TabItem>(window.FindName("ShineLabControlTab"));
                Assert.Equal(Visibility.Collapsed, oldTab.Visibility);
                Assert.Empty(mes.DeviceCommands);
                Assert.Equal(0, mes.ConfigCount);
                window.Close();
            }
            catch (Exception e) { failure = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.Null(failure);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task Finish_without_result_is_explicitly_reported_without_claiming_no_peaks(bool taskFinish, bool endMission, bool expectedMissing)
    {
        var mes = new StubMes { TaskFinishObserved = taskFinish, EndMissionObserved = endMission };
        var vm = new ShineLabControlViewModel(mes, new Confirm());
        await vm.ImportAsync(Fixture);
        await vm.PrepareSelectedAsync();
        await vm.RefreshAsync();
        Assert.Equal(expectedMissing, vm.TaskSummary.Contains("已收到结束通知，但尚未收到 Result"));
        if (expectedMissing)
        {
            Assert.Contains("123456789", vm.TaskSummary);
            Assert.Contains("不代表无峰或零浓度", vm.TaskSummary);
        }
        Assert.Empty(vm.Compounds);
        Assert.Equal(0, mes.ConfigCount);
        Assert.Empty(mes.DeviceCommands);
    }

    [Fact]
    public async Task Empty_result_is_distinct_from_missing_result_and_does_not_claim_no_peaks()
    {
        var mes = new StubMes { TaskFinishObserved = true, ResultJson = "{\"compounds\":[]}" };
        var vm = new ShineLabControlViewModel(mes, new Confirm());
        await vm.ImportAsync(Fixture);
        await vm.PrepareSelectedAsync();
        await vm.RefreshAsync();
        Assert.DoesNotContain("尚未收到 Result", vm.TaskSummary);
        Assert.Contains("Result 的组分列表为空", vm.TaskSummary);
        Assert.Contains("不代表无峰", vm.TaskSummary);
        Assert.Contains("不等于正常结束", vm.TaskSummary);
        Assert.Empty(vm.Compounds);
    }

    [Theory]
    [InlineData(900, true)]
    [InlineData(1400, false)]
    public void Import_layout_switches_between_stacked_and_side_by_side_and_device_mode_hides_tasks(int width, bool compact)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var mes = new StubMes();
                var vm = new ShineLabControlViewModel(mes, new Confirm());
                var view = new Views.ShineLabControlView { DataContext = vm };
                var window = new Window { Content = view, Width = width, Height = 800 };
                window.Show(); window.UpdateLayout();
                var taskGrid = Assert.IsType<DataGrid>(view.FindName("OperatorTaskGrid"));
                var resultGrid = Assert.IsType<DataGrid>(view.FindName("OperatorResultGrid"));
                foreach (var grid in new[] { taskGrid, resultGrid })
                    Assert.DoesNotContain(grid.Columns.OfType<DataGridBoundColumn>(), column =>
                        column.Binding is System.Windows.Data.Binding binding &&
                        new[] { "TaskUuid", "SampleId", "CurrentStage", "Status", "ResultJson", "TaskDetailJson" }.Contains(binding.Path.Path));
                var details = Assert.IsType<ScrollViewer>(view.FindName("ImportDetails"));
                Assert.Equal(compact ? 3 : 1, Grid.GetRow(details));
                Assert.Equal(compact ? 0 : 1, Grid.GetColumn(details));
                Assert.Equal(Visibility.Collapsed, Assert.IsType<TabItem>(view.FindName("DeviceTab")).Visibility);
                view.DisplayMode = "Device"; window.UpdateLayout();
                Assert.Equal(Visibility.Visible, Assert.IsType<TabItem>(view.FindName("DeviceTab")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<TabItem>(view.FindName("ImportTab")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<TabItem>(view.FindName("ResultsTab")).Visibility);
                Assert.Empty(mes.DeviceCommands); Assert.Equal(0, mes.ConfigCount);
                window.Close();
            }
            catch (Exception e) { failure = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.Null(failure);
    }

    [Theory]
    [InlineData("{\"compounds\":[{\"name\":\"TEST\",\"area\":0,\"height\":0,\"concent\":0}]}", "AllZeroQuantitation")]
    [InlineData("{\"compounds\":[{\"name\":\"TEST\",\"area\":\"ND\",\"height\":0,\"concent\":0}]}", "NonQuantified")]
    [InlineData("not-json", "MalformedResult")]
    public async Task Result_review_is_independent_of_task_state_and_does_not_write_devices(string raw, string expected)
    {
        var mes = new StubMes { ResultJson = raw, TaskFinishObserved = true };
        var vm = new ShineLabControlViewModel(mes, new Confirm());
        await vm.ImportAsync(Fixture); await vm.PrepareSelectedAsync();
        mes.Task = mes.Task! with { Status = "AwaitingCompletion" };
        await vm.RefreshAsync();
        Assert.Equal(expected, Assert.Single(vm.ResultReviews).Code);
        Assert.Contains("结果待复核", vm.ResultReviewSummary);
        Assert.Equal("AwaitingCompletion", mes.Task.Status);
        Assert.Equal(raw, Assert.Single(vm.TaskSamples).ResultJson);
        Assert.Empty(mes.DeviceCommands); Assert.Equal(0, mes.ConfigCount);
        vm.SelectedTask = null;
        Assert.Empty(vm.ResultReviews);
    }

    [Fact]
    public async Task Operator_list_uses_sample_names_and_chinese_states_while_audit_details_remain_available()
    {
        var mes = new StubMes { ResultJson = "{\"compounds\":[{\"area\":0,\"height\":0,\"concent\":0}]}" };
        var vm = new ShineLabControlViewModel(mes, new Confirm());
        await vm.ImportAsync(Fixture); await vm.PrepareSelectedAsync();
        mes.Task = mes.Task! with { Status = "AwaitingCompletion" };
        await vm.RefreshAsync();
        var row = Assert.Single(vm.TaskItems);
        Assert.Equal("STD-25_WPF_TEST", row.SampleNames);
        Assert.Equal("STD-25", row.SourceNumbers);
        Assert.Equal("结束状态待确认", row.State);
        Assert.Equal("结果待复核", row.ResultState);
        Assert.DoesNotContain(mes.Task.TaskUuid, vm.OperatorTaskSummary);
        Assert.DoesNotContain("123456789", vm.OperatorTaskSummary);
        Assert.DoesNotContain("AwaitingCompletion", vm.OperatorTaskSummary);
        Assert.Contains("123456789", vm.TaskDetailJson);
        Assert.Contains("123456789", vm.ConfigPreview);
        Assert.DoesNotContain("123456789", vm.OperatorPreparedSummary);
        await vm.RefreshAsync();
        Assert.Equal(Assert.Single(vm.TaskItems), vm.SelectedTaskItem);
        Assert.Empty(mes.DeviceCommands); Assert.Equal(0, mes.ConfigCount);
    }

    private sealed class Confirm : IShineLabOperationConfirmation
    {
        public bool Approved { get; set; } = true;
        public Action? OnConfirm { get; set; }
        public string LastMessage { get; private set; } = "";
        public int Count { get; private set; }
        bool IShineLabOperationConfirmation.Confirm(string title, string message) { Count++; LastMessage = message; OnConfirm?.Invoke(); return Approved; }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpContent> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = respond(request) });
    }
    private sealed class StubMes : IMesClient
    {
        public bool Enabled { get; set; } = true;
        public string Profile { get; set; } = "DcSeqSingleChannel";
        public int Status { get; set; }
        public bool Online { get; set; } = true;
        public DateTimeOffset? LastSeen { get; set; }
        public bool StatusThrows { get; set; }
        public int CreateCount { get; private set; }
        public int ConfigCount { get; private set; }
        public bool ConfigThrows { get; set; }
        public ShineLabTaskResponse? Task { get; set; }
        private ShineLabTaskCreateRequest? _request;
        public string? ResultJson { get; set; }
        public bool TaskFinishObserved { get; set; }
        public bool EndMissionObserved { get; set; }
        public List<ShineLabCommandRequest> DeviceCommands { get; } = [];
        public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken token) => System.Threading.Tasks.Task.FromResult<IReadOnlyList<DashboardTask>>([]);
        public Task<KpiDashboard> GetKpiDashboardAsync(DateOnly date, CancellationToken token) => throw new NotSupportedException();
        public Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<DashboardTask> CreateTaskAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<DashboardTask> CreateTaskAsync(int source, int target, int priority, string? description, string? externalId, CancellationToken token) => throw new NotSupportedException();
        public Task<DashboardTask> MarkArrivedAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task<DashboardTask> ConfirmPickupAsync(Guid id, string name, CancellationToken token) => throw new NotSupportedException();
        public Task<DashboardTask> ConfirmDropoffAsync(Guid id, string name, CancellationToken token) => throw new NotSupportedException();
        public Task<DashboardTask> RetryAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task<DashboardTask> RecoverAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task<DashboardTask> CancelAsync(Guid id, string name, CancellationToken token) => throw new NotSupportedException();
        public Task<ShineLabServerStatusResponse> GetShineLabServerStatusAsync(CancellationToken token) => System.Threading.Tasks.Task.FromResult(new ShineLabServerStatusResponse(true, "192.168.10.11", 5500, 60, 10000, Profile, Enabled));
        public Task<IReadOnlyList<ShineLabDeviceStatusResponse>> GetShineLabDeviceStatusesAsync(CancellationToken token) =>
            StatusThrows ? throw new HttpRequestException("status unavailable") :
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<ShineLabDeviceStatusResponse>>([new("STN61_01", "test", Online, Status,
                Status == 0 ? "Idle" : "Running", Status == 1, null, null, null, null, null, null, null, null, null, LastSeen ?? DateTimeOffset.UtcNow)]);
        public Task<ShineLabTaskResponse> CreateShineLabTaskAsync(ShineLabTaskCreateRequest request, CancellationToken token)
        {
            CreateCount++; _request = request;
            Task ??= new(Guid.NewGuid(), request.TaskUuid, request.EquipmentCode, "Created", "Created", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null);
            return System.Threading.Tasks.Task.FromResult(Task);
        }
        public Task<ShineLabTaskResponse> ConfigureShineLabTaskAsync(string id, CancellationToken token)
        {
            ConfigCount++;
            if (ConfigThrows) throw new HttpRequestException("503 timeout");
            Task = Task! with { Status = "Configured", CurrentStage = "ConfigAcceptedAwaitingVerification" };
            return System.Threading.Tasks.Task.FromResult(Task);
        }
        public Task<ShineLabTaskDetailResponse?> GetShineLabTaskDetailAsync(string id, CancellationToken token) =>
            System.Threading.Tasks.Task.FromResult<ShineLabTaskDetailResponse?>(Task is null ? null : new(Task, [],
                [new(1, _request!.SampleData[0].SampleId, "123456789", _request.SampleData[0].SampleName, ConfigCount > 0, false, TaskFinishObserved, EndMissionObserved, ResultJson, false, null)],
                ResultJson is null ? "AwaitingResults" : "AllResultsReceived"));
        public Task<JsonElement> GetShineLabTaskConfigPreviewAsync(string id, CancellationToken token) =>
            System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(new { taskUuid = id, equipmentCode = "STN61_01", payloadProfile = Profile, strMethod = "Config", body = new { sampleData = new[] { new { sampleId = "123456789" } } } }));
        public Task<IReadOnlyList<ShineLabTaskResponse>> GetShineLabTasksAsync(int limit, CancellationToken token) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<ShineLabTaskResponse>>(Task is null ? [] : [Task]);
        public Task<ShineLabCommandResponse> SendShineLabCommandAsync(string code, ShineLabCommandRequest request, CancellationToken token)
        {
            DeviceCommands.Add(request);
            return System.Threading.Tasks.Task.FromResult(new ShineLabCommandResponse("wire-id", "Command", code, true, null, JsonSerializer.SerializeToElement(new { result = "Success" })));
        }
    }
}
