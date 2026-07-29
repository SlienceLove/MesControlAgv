using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public class MainViewModelTests
{
    [Fact]
    public async Task Refresh_populates_dashboard_and_enables_arrival_for_moving_task()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "MovingToPickup", 0, null);
        using var viewModel = new MainViewModel(new FakeMesClient([task]));

        await viewModel.RefreshAsync();

        Assert.Equal("MES 已连接", viewModel.ConnectionStatus);
        Assert.Equal("在线 / adapter", viewModel.AgvStatus);
        Assert.Single(viewModel.Tasks);
        Assert.True(viewModel.ArriveCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmPickupCommand.CanExecute(null));
    }

    [Fact]
    public async Task Failed_task_enables_retry_and_disables_arrival()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "Failed", 1, "fault");
        using var viewModel = new MainViewModel(new FakeMesClient([task]));

        await viewModel.RefreshAsync();

        Assert.True(viewModel.RetryCommand.CanExecute(null));
        Assert.False(viewModel.ArriveCommand.CanExecute(null));
    }
}

internal sealed class FakeMesClient(IReadOnlyList<DashboardTask> tasks) : IMesClient
{
    public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken) => Task.FromResult(tasks);
    public Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new AgvDashboardSnapshot(true, "adapter", "SAMPLE_01", null));
    public Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken) => Task.FromResult(tasks[0]);
    public Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(tasks[0]);
    public Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromResult(tasks[0]);
    public Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromResult(tasks[0]);
    public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(tasks[0]);
    public Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromResult(tasks[0]);
}
