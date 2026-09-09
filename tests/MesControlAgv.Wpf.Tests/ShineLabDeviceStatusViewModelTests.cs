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
}
