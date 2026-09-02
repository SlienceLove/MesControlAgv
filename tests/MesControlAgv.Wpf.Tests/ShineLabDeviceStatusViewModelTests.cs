using MesControlAgv.Contracts;
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
}
