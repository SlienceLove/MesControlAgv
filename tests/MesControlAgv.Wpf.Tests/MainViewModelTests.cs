using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public class MainViewModelTests
{
    [Fact]
    public void Task_row_exposes_operator_friendly_task_description()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "MovingToPickup", 0, null);

        var row = TaskRowViewModel.From(task);

        Assert.Equal("样品位", row.SourceStationName);
        Assert.Equal("液体前处理工作站", row.TargetStationName);
        Assert.Equal("从样品位取货，运送至液体前处理工作站", row.TaskDescription);
        Assert.Equal("前往取货站", row.StatusDescription);
    }

    [Fact]
    public void Task_row_exposes_created_and_ended_times()
    {
        var createdAt = DateTime.UtcNow.AddMinutes(-5);
        var endedAt = DateTime.UtcNow;
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "Completed", 0, null, CreatedAt: createdAt, EndedAt: endedAt);

        var row = TaskRowViewModel.From(task);

        Assert.Equal(createdAt, row.CreatedAt);
        Assert.Equal(endedAt, row.EndedAt);
    }

    [Fact]
    public void System_error_status_exposes_the_reason_to_the_operator()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "Unknown", 0, "Adapter 通信异常：连接超时");

        var row = TaskRowViewModel.From(task);

        Assert.Equal("系统异常", row.StatusDescription);
        Assert.Equal("原因：Adapter 通信异常：连接超时", row.ErrorDescription);
    }

    [Fact]
    public async Task Refresh_populates_dashboard_and_enables_arrival_for_moving_task()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "MovingToPickup", 0, null);
        var client = new FakeMesClient([task]);
        using var viewModel = new MainViewModel(client);

        await viewModel.RefreshAsync();

        Assert.Equal("MES 已连接", viewModel.ConnectionStatus);
        Assert.Equal("在线 / adapter", viewModel.AgvStatus);
        Assert.Single(viewModel.Tasks);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), client.LastRequestedDate);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), client.LastRequestedKpiDate);
        Assert.Equal(1, viewModel.Kpi.TaskSummary.Total);
        Assert.True(viewModel.ArriveCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmPickupCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refresh_uses_the_selected_task_date()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "MovingToPickup", 0, null);
        var client = new FakeMesClient([task]);
        using var viewModel = new MainViewModel(client)
        {
            TaskFilterDate = DateTime.UtcNow.Date.AddDays(-1)
        };

        await viewModel.RefreshAsync();

        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1)), client.LastRequestedDate);
    }

    [Fact]
    public async Task Refresh_can_select_the_newly_created_task_after_reloading_the_date()
    {
        var existing = new DashboardTask(Guid.NewGuid(), 2, 4, "Completed", 0, null);
        var created = new DashboardTask(Guid.NewGuid(), 2, 4, "MovingToPickup", 0, null);
        using var viewModel = new MainViewModel(new FakeMesClient([existing, created]));

        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync(created.Id);

        Assert.Equal(created.Id, viewModel.SelectedTask?.Id);
    }

    [Fact]
    public async Task Refresh_with_missing_preferred_task_does_not_select_an_old_task()
    {
        var existing = new DashboardTask(Guid.NewGuid(), 2, 4, "Completed", 0, null);
        using var viewModel = new MainViewModel(new FakeMesClient([existing]));

        await viewModel.RefreshAsync(Guid.NewGuid());

        Assert.Null(viewModel.SelectedTask);
    }

    [Fact]
    public async Task Failed_task_enables_retry_and_disables_arrival()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "Failed", 1, "fault");
        using var viewModel = new MainViewModel(new FakeMesClient([task]));

        await viewModel.RefreshAsync();

        Assert.True(viewModel.RetryCommand.CanExecute(null));
        Assert.False(viewModel.ArriveCommand.CanExecute(null));
        Assert.False(viewModel.RecoverCommand.CanExecute(null));
    }

    [Fact]
    public async Task Unknown_task_loads_audit_events_and_enables_recovery()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "Unknown", 0, "device status unavailable");
        using var viewModel = new MainViewModel(new FakeMesClient([task]));

        await viewModel.RefreshAsync();

        Assert.True(viewModel.RecoverCommand.CanExecute(null));
        Assert.Single(viewModel.TaskEvents);
        Assert.Equal("Timeout", viewModel.TaskEvents[0].EventType);
    }
}

internal sealed class FakeMesClient(IReadOnlyList<DashboardTask> tasks) : IMesClient
{
    public DateOnly? LastRequestedDate { get; private set; }
    public DateOnly? LastRequestedKpiDate { get; private set; }

    public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken) => Task.FromResult(tasks);
    public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(DateOnly date, CancellationToken cancellationToken)
    {
        LastRequestedDate = date;
        return Task.FromResult(tasks);
    }
    public Task<KpiDashboard> GetKpiDashboardAsync(DateOnly date, CancellationToken cancellationToken)
    {
        LastRequestedKpiDate = date;
        var completed = tasks.Count(task => task.Status == "Completed");
        var failed = tasks.Count(task => task.Status == "Failed");
        var cancelled = tasks.Count(task => task.Status == "Cancelled");
        var running = tasks.Count - completed - failed - cancelled;
        return Task.FromResult(new KpiDashboard(
            date,
            new KpiTaskSummary(tasks.Count, running, completed, failed, cancelled),
            [],
            new KpiSampleSummary(0, 0, 0, 0, 0, 0, "test"),
            [],
            []));
    }
    public Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult<DashboardTaskDetail?>(new DashboardTaskDetail(
            tasks[0],
            [new DashboardTaskEvent(Guid.NewGuid(), "Timeout", "{\"source\":\"test\"}", DateTime.UtcNow)]));
    public Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new AgvDashboardSnapshot(true, "adapter", "SAMPLE_01", null));
    public Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken) => Task.FromResult(tasks[0]);
    public Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(tasks[0]);
    public Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromResult(tasks[0]);
    public Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromResult(tasks[0]);
    public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(tasks[0]);
    public Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(tasks[0]);
    public Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromResult(tasks[0]);
}
