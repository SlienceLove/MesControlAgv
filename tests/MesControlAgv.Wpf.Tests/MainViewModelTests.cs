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
    public void Paused_task_is_distinguished_in_the_task_monitor()
    {
        var row = TaskRowViewModel.From(new DashboardTask(Guid.NewGuid(), 2, 4, "Paused", 0, null));

        Assert.Contains("\u6682\u505C", row.StatusDescription);
    }

    [Fact]
    public void Task_row_exposes_assignment_and_path_or_explicit_placeholders()
    {
        var assigned = TaskRowViewModel.From(new DashboardTask(
            Guid.NewGuid(),
            2,
            4,
            "MovingToPickup",
            0,
            null,
            ActiveAgvId: "AGV-03",
            ActiveDeviceTaskId: "device-task-3",
            ActivePath: ["SAMPLE_01", "ST_PREP_01"]));
        var pending = TaskRowViewModel.From(new DashboardTask(Guid.NewGuid(), 2, 4, "Created", 0, null));

        Assert.Equal("AGV-03", assigned.AssignedAgvDescription);
        Assert.Equal("device-task-3", assigned.DeviceTaskDescription);
        Assert.Contains("ST_PREP_01", assigned.CurrentPathDescription, StringComparison.Ordinal);
        Assert.Equal("尚未分配", pending.AssignedAgvDescription);
        Assert.Equal("尚未创建", pending.DeviceTaskDescription);
        Assert.Equal("尚未分配执行路径", pending.CurrentPathDescription);
    }

    [Fact]
    public async Task Physical_mode_keeps_status_visible_and_blocks_manual_arrival()
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
        Assert.True(viewModel.IsPhysicalMode);
        Assert.False(viewModel.IsManualArrivalAvailable);
        Assert.Contains("Physical", viewModel.RuntimeMode, StringComparison.Ordinal);
        Assert.False(viewModel.ArriveCommand.CanExecute(null));
        viewModel.ArriveCommand.Execute(null);
        Assert.Equal(0, client.MarkArrivedCallCount);
        Assert.False(viewModel.ConfirmPickupCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refresh_displays_correlated_mes_and_device_execution_state()
    {
        var taskId = Guid.NewGuid();
        var client = new FakeMesClient([
            new DashboardTask(taskId, 2, 4, "MovingToPickup", 0, null, ActiveAgvId: "AGV-01")
        ])
        {
            FleetStatus = [new AgvFleetDashboardStatus(
                new AgvDashboardSnapshot(true, "adapter", "SAMPLE_01", taskId),
                new AgvActiveTaskStatus(
                    taskId,
                    Guid.NewGuid(),
                    "MovingToPickup",
                    "device-01",
                    "moving",
                    "ST_PREP_01",
                    null,
                    ["SAMPLE_01", "ST_PREP_01"]))]
        };
        using var viewModel = new MainViewModel(client);

        await viewModel.RefreshAsync();

        Assert.Contains("MovingToPickup", viewModel.AgvExecutionStatus, StringComparison.Ordinal);
        Assert.Contains("moving", viewModel.AgvExecutionStatus, StringComparison.Ordinal);
        Assert.Contains("ST_PREP_01", viewModel.AgvExecutionStatus, StringComparison.Ordinal);
        Assert.Equal("MovingToPickup", viewModel.SelectedAgv?.MesTaskStatus);
        Assert.Equal("moving", viewModel.SelectedAgv?.DeviceState);
        Assert.Equal("ST_PREP_01", viewModel.SelectedAgv?.TargetStationId);
        Assert.Contains("ST_PREP_01", viewModel.SelectedAgv?.ExecutionPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selected_agv_pause_command_is_sent_through_mes_to_adapter()
    {
        var operationId = Guid.NewGuid();
        var client = new FakeMesClient([
            new DashboardTask(Guid.NewGuid(), 2, 4, "MovingToPickup", 0, null, ActiveAgvId: "AGV-01", ActiveDeviceTaskId: operationId.ToString("N"))
        ])
        {
            FleetStatus = [new AgvFleetDashboardStatus(
                new AgvDashboardSnapshot(true, "adapter", "SAMPLE_01", operationId),
                null)],
            CommandResult = new AgvCommandResult(operationId, operationId.ToString("N"), "SAMPLE_01", "paused", null)
        };
        using var viewModel = new MainViewModel(client);

        await viewModel.RefreshAsync();

        Assert.True(viewModel.PauseAgvCommand.CanExecute(null));
        viewModel.PauseAgvCommand.Execute(null);
        await WaitUntilAsync(() => client.AgvCommandCallCount == 1);

        Assert.Equal("AGV-01", client.LastAgvCommand?.AgvId);
        Assert.Equal("pause", client.LastAgvCommand?.Command);
        Assert.Equal(operationId, client.LastAgvCommand?.TaskId);
    }

    [Fact]
    public async Task Task_commands_are_single_flight_across_different_actions()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "Created", 0, null);
        var dispatchGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeMesClient([task])
        {
            DispatchGate = dispatchGate
        };
        using var viewModel = new MainViewModel(client);

        await viewModel.RefreshAsync();
        Assert.True(viewModel.DispatchTaskCommand.CanExecute(null));

        viewModel.DispatchTaskCommand.Execute(null);
        await WaitUntilAsync(() => client.DispatchCallCount == 1 && viewModel.IsActionInProgress);

        Assert.False(viewModel.DispatchTaskCommand.CanExecute(null));
        Assert.False(viewModel.QueryTasksCommand.CanExecute(null));
        viewModel.DispatchTaskCommand.Execute(null);
        await Task.Delay(20);
        Assert.Equal(1, client.DispatchCallCount);

        dispatchGate.SetResult(true);
        await WaitUntilAsync(() => !viewModel.IsActionInProgress);
        Assert.Equal(string.Empty, viewModel.CurrentAction);
        Assert.Equal(1, client.DispatchCallCount);
    }

    [Fact]
    public async Task Agv_command_failure_is_visible_and_releases_the_action_gate()
    {
        var operationId = Guid.NewGuid();
        var client = new FakeMesClient([
            new DashboardTask(Guid.NewGuid(), 2, 4, "MovingToPickup", 0, null, ActiveAgvId: "AGV-01", ActiveDeviceTaskId: operationId.ToString("N"))
        ])
        {
            FleetStatus = [new AgvFleetDashboardStatus(
                new AgvDashboardSnapshot(true, "adapter", "SAMPLE_01", operationId),
                null)],
            CommandResult = new AgvCommandResult(operationId, operationId.ToString("N"), "SAMPLE_01", "failed", "blocked by safety interlock")
        };
        using var viewModel = new MainViewModel(client);

        await viewModel.RefreshAsync();
        viewModel.PauseAgvCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsActionInProgress);

        Assert.Equal("\u64CD\u4F5C\u5931\u8D25", viewModel.ActionStatus);
        Assert.Contains("blocked by safety interlock", viewModel.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, viewModel.CurrentAction);
        Assert.True(viewModel.PauseAgvCommand.CanExecute(null));
    }

    [Fact]
    public async Task Simulator_mode_respects_the_build_safety_boundary()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "MovingToPickup", 0, null);
        var client = new FakeMesClient([task]);
        var simulator = new RecordingSimulatorControlClient();
        using var viewModel = new MainViewModel(client, simulator);

        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsSimulatorMode);
#if DEBUG
        Assert.True(viewModel.IsManualArrivalAvailable);
        Assert.True(viewModel.ArriveCommand.CanExecute(null));

        viewModel.ArriveCommand.Execute(null);
        await WaitUntilAsync(() => client.MarkArrivedCallCount == 1);

        Assert.Equal(1, simulator.TaskControlCallCount);
#else
        Assert.False(viewModel.IsManualArrivalAvailable);
        Assert.False(viewModel.ArriveCommand.CanExecute(null));

        viewModel.ArriveCommand.Execute(null);

        Assert.Equal(0, client.MarkArrivedCallCount);
        Assert.Equal(0, simulator.TaskControlCallCount);
#endif
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("The expected asynchronous command did not complete.");
    }
}

internal sealed class FakeMesClient(IReadOnlyList<DashboardTask> tasks) : IMesClient
{
    private readonly List<DashboardStation> _stations = [];

    public DateOnly? LastRequestedDate { get; private set; }
    public DateOnly? LastRequestedKpiDate { get; private set; }
    public int GetStationsCallCount { get; private set; }
    public int MarkArrivedCallCount { get; private set; }
    public int DispatchCallCount { get; private set; }
    public Guid? LastDispatchTaskId { get; private set; }
    public DashboardTask? DispatchResult { get; set; }
    public Exception? DispatchException { get; set; }
    public TaskCompletionSource<bool>? DispatchGate { get; set; }
    public IReadOnlyList<AgvFleetDashboardStatus>? FleetStatus { get; set; }
    public AgvCommandResult? CommandResult { get; set; }
    public int AgvCommandCallCount { get; private set; }
    public (string AgvId, string Command, Guid? TaskId)? LastAgvCommand { get; private set; }
    public DashboardPlannedPath? PlannedPath { get; set; }
    public (string FromStationId, string ToStationId, IReadOnlyCollection<string>? BlockedStations)? LastPlanRequest { get; private set; }
    public (int SourceStationCode, int TargetStationCode, int Priority, string? Description, string? ExternalId)? LastCreateRequest { get; private set; }
    public IReadOnlyList<DashboardStation> Stations => _stations;

    public void SetStations(params DashboardStation[] stations)
    {
        _stations.Clear();
        _stations.AddRange(stations);
    }

    public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken) => Task.FromResult(CurrentTasks());
    public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(DateOnly date, CancellationToken cancellationToken)
    {
        LastRequestedDate = date;
        return Task.FromResult(CurrentTasks());
    }
    public Task<KpiDashboard> GetKpiDashboardAsync(DateOnly date, CancellationToken cancellationToken)
    {
        LastRequestedKpiDate = date;
        var currentTasks = CurrentTasks();
        var completed = currentTasks.Count(task => task.Status == "Completed");
        var failed = currentTasks.Count(task => task.Status == "Failed");
        var cancelled = currentTasks.Count(task => task.Status == "Cancelled");
        var running = currentTasks.Count - completed - failed - cancelled;
        return Task.FromResult(new KpiDashboard(
            date,
            new KpiTaskSummary(currentTasks.Count, running, completed, failed, cancelled),
            [],
            new KpiSampleSummary(0, 0, 0, 0, 0, 0, "test"),
            [],
            []));
    }
    public Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult<DashboardTaskDetail?>(new DashboardTaskDetail(
            CurrentTasks()[0],
            [new DashboardTaskEvent(Guid.NewGuid(), "Timeout", "{\"source\":\"test\"}", DateTime.UtcNow)]));
    public Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new AgvDashboardSnapshot(true, "adapter", "SAMPLE_01", null));
    public Task<IReadOnlyList<AgvFleetDashboardStatus>> GetAgvFleetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(FleetStatus ?? [new AgvFleetDashboardStatus(new AgvDashboardSnapshot(true, "adapter", "SAMPLE_01", null), null)]);
    public Task<AgvCommandResult?> ExecuteAgvCommandAsync(string agvId, string command, Guid? taskId, CancellationToken cancellationToken)
    {
        AgvCommandCallCount++;
        LastAgvCommand = (agvId, command, taskId);
        return Task.FromResult(CommandResult);
    }
    public Task<IReadOnlyList<DashboardStation>> GetStationsAsync(CancellationToken cancellationToken)
    {
        GetStationsCallCount++;
        return Task.FromResult<IReadOnlyList<DashboardStation>>(_stations);
    }
    public Task<DashboardPlannedPath> PlanPathAsync(
        string fromStationId,
        string toStationId,
        IReadOnlyCollection<string>? blockedStations,
        CancellationToken cancellationToken)
    {
        LastPlanRequest = (fromStationId, toStationId, blockedStations);
        return Task.FromResult(PlannedPath ?? new DashboardPlannedPath([fromStationId, toStationId], 1));
    }
    public Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken) => Task.FromResult(tasks[0]);
    public Task<DashboardTask> CreateTaskAsync(int sourceStationCode, int targetStationCode, int priority, string? description, string? externalId, CancellationToken cancellationToken)
    {
        LastCreateRequest = (sourceStationCode, targetStationCode, priority, description, externalId);
        return Task.FromResult(CurrentTasks()[0]);
    }
    public Task<DashboardTask> DispatchTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        DispatchCallCount++;
        LastDispatchTaskId = taskId;
        if (DispatchException is { } exception) return Task.FromException<DashboardTask>(exception);
        return DispatchTaskCoreAsync();

        async Task<DashboardTask> DispatchTaskCoreAsync()
        {
            if (DispatchGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            return DispatchResult ?? CurrentTasks()[0];
        }
    }
    public Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken)
    {
        MarkArrivedCallCount++;
        return Task.FromResult(CurrentTasks()[0]);
    }
    public Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromResult(CurrentTasks()[0]);
    public Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromResult(CurrentTasks()[0]);
    public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(CurrentTasks()[0]);
    public Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(CurrentTasks()[0]);
    public Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromResult(CurrentTasks()[0]);

    private IReadOnlyList<DashboardTask> CurrentTasks() => DispatchCallCount > 0 && DispatchResult is { } result ? [result] : tasks;
}

internal sealed class RecordingSimulatorControlClient : ISimulatorControlClient
{
    public int TaskControlCallCount { get; private set; }

    public Task ApplyControlAsync(string mode, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ApplyControlAsync(Guid deviceTaskId, string mode, CancellationToken cancellationToken)
    {
        TaskControlCallCount++;
        return Task.CompletedTask;
    }
}
