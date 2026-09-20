using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class ShineLabDeviceStatusViewModelTests
{
    [Fact]
    public async Task Refresh_lists_pushed_devices_and_projects_selected_task_state()
    {
        var mes = new FakeMesClient([])
        {
            ShineLabDeviceStatuses =
            [
                new ShineLabDeviceStatusResponse(
                    "SHA18I", "SHA-18i 自动进样器", true, 1, "Running", true,
                    "task-01", "S-01", "标准样", "A", 11, "Injecting", 40,
                    null, null, DateTimeOffset.UtcNow)
            ]
        };
        var viewModel = new ShineLabDeviceStatusViewModel(mes);

        await viewModel.RefreshAsync();

        Assert.Single(viewModel.Devices);
        Assert.Equal("SHA18I", viewModel.SelectedEquipmentCode);
        Assert.Equal("在线", viewModel.SelectedOnline);
        Assert.Equal("有任务进行中", viewModel.SelectedTaskState);
        Assert.Equal("标准样 (S-01)", viewModel.SelectedSample);
        Assert.Equal("通道 A / 位置 11", viewModel.SelectedChannelPosition);
        Assert.Equal("Injecting", viewModel.SelectedStage);
        Assert.Equal("40%", viewModel.SelectedProgress);
    }

    [Fact]
    public async Task Refresh_with_no_pushes_explains_that_shinelab_has_not_reported()
    {
        var viewModel = new ShineLabDeviceStatusViewModel(new FakeMesClient([]));

        await viewModel.RefreshAsync();

        Assert.Empty(viewModel.Devices);
        Assert.Equal("暂无推送设备", viewModel.ConnectionStatus);
        Assert.Contains("尚未收到 ShineLab", viewModel.Message);
    }

    [Fact]
    public async Task Ion_chromatography_uses_all_shinelab_statuses_without_name_based_workstation_filtering()
    {
        var mes = new FakeMesClient([])
        {
            ShineLabDeviceStatuses =
            [
                new ShineLabDeviceStatusResponse(
                    "CIC-D160", "D160+", true, 1, "Idle", false,
                    null, null, null, null, null, null, null, null, null,
                    DateTimeOffset.UtcNow),
                new ShineLabDeviceStatusResponse(
                    "OPEN-CAP", "开盖分液", true, 1, "Running", true,
                    "task-02", null, null, null, null, null, null, null, null,
                    DateTimeOffset.UtcNow)
            ]
        };
        var viewModel = new ShineLabDeviceStatusViewModel(mes);

        await viewModel.RefreshAsync();

        Assert.Equal(2, viewModel.VisibleDevices.Count());
        Assert.Equal(2, viewModel.IonSubdevices.Count);
        Assert.Contains(viewModel.IonSubdevices, item => item.DeviceName == "开盖分液");

        viewModel.SelectedInstrument = ShineLabDeviceStatusViewModel.SampleWorkstationInstrument;

        Assert.Empty(viewModel.VisibleDevices);
        Assert.Null(viewModel.SelectedDevice);
    }

    [Fact]
    public async Task Opening_workstation_uses_read_only_snapshot_and_maps_latest_task()
    {
        var observedAt = DateTimeOffset.Parse("2026-09-09T03:04:05Z");
        var mes = new FakeMesClient([])
        {
            SampleWorkstationSnapshot = new SampleWorkstationDashboardSnapshot(
                new SampleWorkstationStatusResponse(
                    "SAMPLE-WORKSTATION-01", "OWS-01", true,
                    SampleWorkstationDeviceState.Running, 1, observedAt),
                new SampleWorkstationErrorResponse(
                    "SAMPLE-WORKSTATION-01", 17, "安全门未关闭", true, observedAt),
                [
                    new SampleWorkstationTaskSummaryResponse(2, "TASK-02", "较早任务", SampleWorkstationTaskState.Completed, "完成", "2026-09-08 10:00:00", null),
                    new SampleWorkstationTaskSummaryResponse(9, "TASK-09", "当前任务", SampleWorkstationTaskState.Running, "运行", "2026-09-09 11:00:00", "现场批次")
                ])
        };
        var viewModel = new ShineLabDeviceStatusViewModel(mes)
        {
            SelectedInstrument = ShineLabDeviceStatusViewModel.SampleWorkstationInstrument
        };

        await viewModel.RefreshAsync();

        Assert.Equal(ShineLabDeviceStatusViewModel.DefaultSampleWorkstationDeviceId, mes.LastSampleWorkstationDeviceId);
        Assert.Equal("在线", viewModel.WorkstationOnline);
        Assert.Equal("运行", viewModel.WorkstationState);
        Assert.Equal("[17] 安全门未关闭", viewModel.WorkstationError);
        Assert.Equal("2026-09-09 11:00:00", viewModel.WorkstationLatestTaskTime);
        Assert.Equal("TASK-09", viewModel.WorkstationLatestTaskNo);
        Assert.Equal("运行", viewModel.WorkstationLatestTaskState);
        Assert.Equal("TASK-09", viewModel.WorkstationTasks[0].TaskNo);
        Assert.Empty(viewModel.VisibleDevices);

        viewModel.SelectedInstrument = ShineLabDeviceStatusViewModel.IonChromatographyInstrument;

        Assert.Equal("待刷新离子色谱状态", viewModel.ConnectionStatus);
        Assert.DoesNotContain("开盖分液", viewModel.Message);
    }

    [Fact]
    public async Task Unknown_workstation_state_is_not_reported_as_online()
    {
        var mes = new FakeMesClient([])
        {
            SampleWorkstationSnapshot = new SampleWorkstationDashboardSnapshot(
                new SampleWorkstationStatusResponse(
                    "SAMPLE-WORKSTATION-01", "OWS-01", true,
                    SampleWorkstationDeviceState.Unknown, 99, DateTimeOffset.UtcNow),
                null,
                [])
        };
        var viewModel = new ShineLabDeviceStatusViewModel(mes)
        {
            SelectedInstrument = ShineLabDeviceStatusViewModel.SampleWorkstationInstrument
        };

        await viewModel.RefreshAsync();

        Assert.Equal("未知", viewModel.WorkstationOnline);
        Assert.Equal("状态未知，请确认协议数据", viewModel.WorkstationConnectionStatus);
    }

    [Fact]
    public async Task Partial_workstation_refresh_retains_last_successful_sections_and_selection()
    {
        var mes = new FakeMesClient([])
        {
            SampleWorkstationSnapshot = IdleSnapshot(SampleWorkstationTaskState.Completed)
        };
        var viewModel = CreateTestControlViewModel(mes, new WorkstationConfirmationStub(result: true));
        await viewModel.RefreshAsync();
        viewModel.SelectedWorkstationTask = Assert.Single(viewModel.WorkstationTasks);
        mes.SampleWorkstationSnapshot = new SampleWorkstationDashboardSnapshot(
            null,
            null,
            [],
            StatusReadError: "状态接口暂不可用",
            ErrorReadError: "错误接口暂不可用",
            TasksReadError: "任务接口暂不可用");

        await viewModel.RefreshAsync();

        Assert.Equal(SampleWorkstationDeviceState.Idle, viewModel.WorkstationStatus?.State);
        Assert.Equal(0, viewModel.WorkstationErrorResponse?.ErrorCode);
        Assert.Equal("TEST-001", Assert.Single(viewModel.WorkstationTasks).TaskNo);
        Assert.Equal("TEST-001", viewModel.SelectedWorkstationTask?.TaskNo);
        Assert.Contains("状态接口暂不可用", viewModel.WorkstationReadErrors);
        Assert.Contains("任务接口暂不可用", viewModel.WorkstationReadErrors);
        Assert.False(viewModel.StartWorkstationTestTaskCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_refresh_started_for_old_instrument_cannot_overwrite_a_new_selection()
    {
        var gate = new TaskCompletionSource<SampleWorkstationDashboardSnapshot?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mes = new FakeMesClient([])
        {
            SampleWorkstationSnapshotGate = gate
        };
        var viewModel = new ShineLabDeviceStatusViewModel(mes)
        {
            SelectedInstrument = ShineLabDeviceStatusViewModel.SampleWorkstationInstrument
        };

        var refresh = viewModel.RefreshAsync();
        viewModel.SelectedInstrument = ShineLabDeviceStatusViewModel.IonChromatographyInstrument;
        gate.SetResult(new SampleWorkstationDashboardSnapshot(
            new SampleWorkstationStatusResponse(
                "SAMPLE-WORKSTATION-01", "OWS-01", true,
                SampleWorkstationDeviceState.Running, 1, DateTimeOffset.UtcNow),
            null,
            []));

        await refresh;

        Assert.Equal(ShineLabDeviceStatusViewModel.IonChromatographyInstrument, viewModel.SelectedInstrument);
        Assert.Null(viewModel.SampleWorkstationSnapshot);
        Assert.Empty(viewModel.WorkstationTasks);
    }

    [Fact]
    public void Workstation_test_control_is_hidden_and_disabled_by_default()
    {
        var viewModel = new ShineLabDeviceStatusViewModel(new FakeMesClient([]));

        Assert.False(viewModel.IsWorkstationTestControlVisible);
        Assert.False(viewModel.StartWorkstationTestTaskCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancelling_workstation_test_confirmation_sends_no_start_request()
    {
        var mes = new FakeMesClient([])
        {
            SampleWorkstationSnapshot = IdleSnapshot(SampleWorkstationTaskState.Completed)
        };
        var confirmation = new WorkstationConfirmationStub(result: false);
        var viewModel = CreateTestControlViewModel(mes, confirmation);
        await viewModel.RefreshAsync();
        viewModel.SelectedWorkstationTask = Assert.Single(viewModel.WorkstationTasks);
        Assert.True(viewModel.StartWorkstationTestTaskCommand.CanExecute(null));

        await viewModel.StartSelectedWorkstationTestTaskAsync();

        Assert.Equal(0, mes.SampleWorkstationStartCallCount);
        Assert.Equal(1, confirmation.CallCount);
        Assert.Contains("未发送请求", viewModel.WorkstationTestControlMessage);
    }

    [Fact]
    public async Task Confirmed_workstation_test_starts_once_and_observes_running_before_completion()
    {
        var mes = new FakeMesClient([])
        {
            SampleWorkstationSnapshot = IdleSnapshot(SampleWorkstationTaskState.Completed)
        };
        mes.SampleWorkstationSnapshots.Enqueue(IdleSnapshot(SampleWorkstationTaskState.Completed));
        mes.SampleWorkstationSnapshots.Enqueue(RunningSnapshot());
        mes.SampleWorkstationSnapshots.Enqueue(IdleSnapshot(SampleWorkstationTaskState.Completed));
        var confirmation = new WorkstationConfirmationStub(result: true);
        var viewModel = CreateTestControlViewModel(mes, confirmation, maximumObservationPolls: 3);
        await viewModel.RefreshAsync();
        viewModel.SelectedWorkstationTask = Assert.Single(viewModel.WorkstationTasks);

        await viewModel.StartSelectedWorkstationTestTaskAsync();

        Assert.Equal(1, mes.SampleWorkstationStartCallCount);
        Assert.Equal(
            (ShineLabDeviceStatusViewModel.DefaultSampleWorkstationDeviceId, "TEST-001"),
            mes.LastSampleWorkstationStart);
        Assert.Equal("任务完成", viewModel.WorkstationTestControlMessage);
        Assert.False(viewModel.IsWorkstationTestBusy);
    }

    [Fact]
    public async Task Old_completed_state_times_out_without_completing_or_retrying_start()
    {
        var mes = new FakeMesClient([])
        {
            SampleWorkstationSnapshot = IdleSnapshot(SampleWorkstationTaskState.Completed)
        };
        var viewModel = CreateTestControlViewModel(
            mes,
            new WorkstationConfirmationStub(result: true),
            maximumObservationPolls: 2);
        await viewModel.RefreshAsync();
        viewModel.SelectedWorkstationTask = Assert.Single(viewModel.WorkstationTasks);

        await viewModel.StartSelectedWorkstationTestTaskAsync();

        Assert.Equal(1, mes.SampleWorkstationStartCallCount);
        Assert.Contains("终态尚未确认", viewModel.WorkstationTestControlMessage);
        Assert.DoesNotContain("任务完成", viewModel.WorkstationTestControlMessage);
    }

    [Fact]
    public async Task Workstation_test_start_failure_is_reported_without_retry()
    {
        var mes = new FakeMesClient([])
        {
            SampleWorkstationSnapshot = IdleSnapshot(SampleWorkstationTaskState.Completed),
            SampleWorkstationStartException = new HttpRequestException("厂家接口连接中断")
        };
        var viewModel = CreateTestControlViewModel(mes, new WorkstationConfirmationStub(result: true));
        await viewModel.RefreshAsync();
        viewModel.SelectedWorkstationTask = Assert.Single(viewModel.WorkstationTasks);

        await viewModel.StartSelectedWorkstationTestTaskAsync();

        Assert.Equal(1, mes.SampleWorkstationStartCallCount);
        Assert.Contains("启动结果不明确", viewModel.WorkstationTestControlMessage);
        Assert.Contains("厂家接口连接中断", viewModel.WorkstationTestControlMessage);
    }

    [Fact]
    public async Task Unacknowledged_workstation_test_start_does_not_begin_observation()
    {
        var mes = new FakeMesClient([])
        {
            SampleWorkstationSnapshot = IdleSnapshot(SampleWorkstationTaskState.Completed),
            SampleWorkstationStartResult = new SampleWorkstationCommandResponse(
                ShineLabDeviceStatusViewModel.DefaultSampleWorkstationDeviceId,
                SampleWorkstationCommandOperation.StartTask,
                200,
                System.Text.Json.JsonSerializer.SerializeToElement("启动失败"),
                DateTimeOffset.UtcNow)
            {
                TaskNo = "TEST-001",
                Acknowledged = false
            }
        };
        var viewModel = CreateTestControlViewModel(mes, new WorkstationConfirmationStub(result: true));
        await viewModel.RefreshAsync();
        viewModel.SelectedWorkstationTask = Assert.Single(viewModel.WorkstationTasks);

        await viewModel.StartSelectedWorkstationTestTaskAsync();

        Assert.Equal(1, mes.SampleWorkstationStartCallCount);
        Assert.Equal(1, mes.SampleWorkstationSnapshotCallCount);
        Assert.Contains("未确认", viewModel.WorkstationTestControlMessage);
    }

    [Fact]
    public async Task Explicit_workstation_test_rejection_is_not_reported_as_unknown()
    {
        var mes = new FakeMesClient([])
        {
            SampleWorkstationSnapshot = IdleSnapshot(SampleWorkstationTaskState.Completed),
            SampleWorkstationStartException = new SampleWorkstationTestStartException(
                "工作站控制未启用",
                outcomeUnknown: false)
        };
        var viewModel = CreateTestControlViewModel(mes, new WorkstationConfirmationStub(result: true));
        await viewModel.RefreshAsync();
        viewModel.SelectedWorkstationTask = Assert.Single(viewModel.WorkstationTasks);

        await viewModel.StartSelectedWorkstationTestTaskAsync();

        Assert.Equal(1, mes.SampleWorkstationStartCallCount);
        Assert.Contains("启动请求失败", viewModel.WorkstationTestControlMessage);
        Assert.DoesNotContain("结果不明确", viewModel.WorkstationTestControlMessage);
    }

    [Fact]
    public async Task Cancelling_local_observation_does_not_send_a_stop_or_second_start()
    {
        using var cancellation = new CancellationTokenSource();
        var mes = new FakeMesClient([])
        {
            SampleWorkstationSnapshot = IdleSnapshot(SampleWorkstationTaskState.Completed)
        };
        mes.SampleWorkstationSnapshots.Enqueue(IdleSnapshot(SampleWorkstationTaskState.Completed));
        mes.SampleWorkstationSnapshots.Enqueue(RunningSnapshot());
        mes.SampleWorkstationSnapshotObserved = callCount =>
        {
            if (callCount == 2) cancellation.Cancel();
        };
        var viewModel = CreateTestControlViewModel(
            mes,
            new WorkstationConfirmationStub(result: true),
            maximumObservationPolls: 2,
            observationInterval: TimeSpan.FromSeconds(1));
        await viewModel.RefreshAsync();
        viewModel.SelectedWorkstationTask = Assert.Single(viewModel.WorkstationTasks);

        await viewModel.StartSelectedWorkstationTestTaskAsync(cancellation.Token);

        Assert.Equal(1, mes.SampleWorkstationStartCallCount);
        Assert.Contains("本地状态观察", viewModel.WorkstationTestControlMessage);
        Assert.Contains("设备任务未被停止", viewModel.WorkstationTestControlMessage);
    }

    [Fact]
    public async Task Workstation_observation_uses_an_absolute_timeout()
    {
        var mes = new FakeMesClient([])
        {
            SampleWorkstationSnapshot = IdleSnapshot(SampleWorkstationTaskState.Completed)
        };
        var viewModel = CreateTestControlViewModel(
            mes,
            new WorkstationConfirmationStub(result: true),
            maximumObservationPolls: 300,
            observationInterval: TimeSpan.FromSeconds(1),
            observationTimeout: TimeSpan.FromMilliseconds(20));
        await viewModel.RefreshAsync();
        viewModel.SelectedWorkstationTask = Assert.Single(viewModel.WorkstationTasks);

        await viewModel.StartSelectedWorkstationTestTaskAsync();

        Assert.Equal(1, mes.SampleWorkstationStartCallCount);
        Assert.Contains("终态尚未确认", viewModel.WorkstationTestControlMessage);
        Assert.True(mes.SampleWorkstationSnapshotCallCount < 300);
    }

    [Fact]
    public async Task Workstation_observation_recovers_after_partial_snapshot_without_losing_task()
    {
        var mes = new FakeMesClient([])
        {
            SampleWorkstationSnapshot = IdleSnapshot(SampleWorkstationTaskState.Completed)
        };
        mes.SampleWorkstationSnapshots.Enqueue(IdleSnapshot(SampleWorkstationTaskState.Completed));
        mes.SampleWorkstationSnapshots.Enqueue(RunningSnapshot());
        mes.SampleWorkstationSnapshots.Enqueue(new SampleWorkstationDashboardSnapshot(
            null,
            null,
            [],
            StatusReadError: "状态读取超时",
            ErrorReadError: "错误读取超时",
            TasksReadError: "任务读取超时"));
        mes.SampleWorkstationSnapshots.Enqueue(IdleSnapshot(SampleWorkstationTaskState.Completed));
        var viewModel = CreateTestControlViewModel(
            mes,
            new WorkstationConfirmationStub(result: true),
            maximumObservationPolls: 3);
        await viewModel.RefreshAsync();
        viewModel.SelectedWorkstationTask = Assert.Single(viewModel.WorkstationTasks);

        await viewModel.StartSelectedWorkstationTestTaskAsync();

        Assert.Equal(1, mes.SampleWorkstationStartCallCount);
        Assert.Equal("任务完成", viewModel.WorkstationTestControlMessage);
        Assert.Equal("TEST-001", viewModel.SelectedWorkstationTask?.TaskNo);
    }

    [Fact]
    public async Task Disposing_during_workstation_observation_cancels_without_read_failure()
    {
        var mes = new FakeMesClient([])
        {
            SampleWorkstationSnapshot = IdleSnapshot(SampleWorkstationTaskState.Completed)
        };
        mes.SampleWorkstationSnapshots.Enqueue(IdleSnapshot(SampleWorkstationTaskState.Completed));
        mes.SampleWorkstationSnapshots.Enqueue(RunningSnapshot());
        ShineLabDeviceStatusViewModel? viewModel = null;
        mes.SampleWorkstationSnapshotObserved = callCount =>
        {
            if (callCount == 2) viewModel?.Dispose();
        };
        viewModel = CreateTestControlViewModel(
            mes,
            new WorkstationConfirmationStub(result: true),
            maximumObservationPolls: 2,
            observationInterval: TimeSpan.FromSeconds(1));
        await viewModel.RefreshAsync();
        viewModel.SelectedWorkstationTask = Assert.Single(viewModel.WorkstationTasks);

        await viewModel.StartSelectedWorkstationTestTaskAsync();

        Assert.Equal(1, mes.SampleWorkstationStartCallCount);
        Assert.False(viewModel.IsWorkstationTestBusy);
        Assert.DoesNotContain("状态读取失败", viewModel.WorkstationTestControlMessage);
    }

    private static ShineLabDeviceStatusViewModel CreateTestControlViewModel(
        FakeMesClient mes,
        ISampleWorkstationTestConfirmation confirmation,
        int maximumObservationPolls = 1,
        TimeSpan? observationInterval = null,
        TimeSpan? observationTimeout = null) =>
        new(
            mes,
            sampleWorkstationTestControlEnabled: true,
            sampleWorkstationTestConfirmation: confirmation,
            workstationObservationInterval: observationInterval ?? TimeSpan.Zero,
            workstationObservationTimeout: observationTimeout,
            maximumObservationPolls: maximumObservationPolls)
        {
            SelectedInstrument = ShineLabDeviceStatusViewModel.SampleWorkstationInstrument
        };

    private static SampleWorkstationDashboardSnapshot IdleSnapshot(
        SampleWorkstationTaskState taskState) =>
        new(
            new SampleWorkstationStatusResponse(
                ShineLabDeviceStatusViewModel.DefaultSampleWorkstationDeviceId,
                "TEST-001",
                true,
                SampleWorkstationDeviceState.Idle,
                0,
                DateTimeOffset.UtcNow),
            new SampleWorkstationErrorResponse(
                ShineLabDeviceStatusViewModel.DefaultSampleWorkstationDeviceId,
                0,
                "TaskCompleted",
                true,
                DateTimeOffset.UtcNow),
            [new SampleWorkstationTaskSummaryResponse(
                1,
                "TEST-001",
                "TEST-001",
                taskState,
                taskState == SampleWorkstationTaskState.Completed ? "任务完成" : "正在运行",
                "2026-09-15 16:30:00",
                null)]);

    private static SampleWorkstationDashboardSnapshot RunningSnapshot() =>
        new(
            new SampleWorkstationStatusResponse(
                ShineLabDeviceStatusViewModel.DefaultSampleWorkstationDeviceId,
                "TEST-001",
                true,
                SampleWorkstationDeviceState.Running,
                1,
                DateTimeOffset.UtcNow),
            new SampleWorkstationErrorResponse(
                ShineLabDeviceStatusViewModel.DefaultSampleWorkstationDeviceId,
                3,
                "ExperimentStarted",
                true,
                DateTimeOffset.UtcNow),
            [new SampleWorkstationTaskSummaryResponse(
                1,
                "TEST-001",
                "TEST-001",
                SampleWorkstationTaskState.Running,
                "正在运行",
                "2026-09-15 16:30:00",
                null)]);

    private sealed class WorkstationConfirmationStub(bool result) : ISampleWorkstationTestConfirmation
    {
        public int CallCount { get; private set; }

        public bool Confirm(string title, string message)
        {
            CallCount++;
            Assert.Contains("TEST-001", message, StringComparison.Ordinal);
            return result;
        }
    }
}
