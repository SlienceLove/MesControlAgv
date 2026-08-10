using MesControlAgv.Contracts;
using MesControlAgv.Domain;
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
    public async Task Refresh_keeps_last_successful_task_and_fleet_snapshot_visible_until_replacement_completes()
    {
        var taskId = Guid.NewGuid();
        var operationId = TransportOperationIds.Pickup(taskId);
        var task = new DashboardTask(
            taskId,
            2,
            4,
            "MovingToPickup",
            0,
            null,
            ActiveAgvId: "AGV-01",
            ActiveDeviceTaskId: "device-pickup",
            ActivePath: ["SAMPLE_01", "ST_PREP_01"]);
        var client = new FakeMesClient([task])
        {
            FleetStatus =
            [
                new AgvFleetDashboardStatus(
                    new AgvDashboardSnapshot(true, "adapter", "SAMPLE_01", operationId),
                    new AgvActiveTaskStatus(
                        taskId,
                        operationId,
                        "MovingToPickup",
                        "device-pickup",
                        "moving",
                        "ST_PREP_01",
                        null,
                        ["SAMPLE_01", "ST_PREP_01"]))
            ]
        };
        using var viewModel = new MainViewModel(client);

        await viewModel.RefreshAsync();
        Assert.Equal(taskId, viewModel.SelectedTask?.Id);
        Assert.Equal(taskId, viewModel.SelectedAgv?.MesTransportTaskId);
        Assert.Equal(operationId, viewModel.SelectedAgv?.CurrentTaskId);

        var refreshEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.TaskSnapshotEntered = refreshEntered;
        client.TaskSnapshotGate = refreshRelease;

        var refreshTask = viewModel.RefreshAsync();
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.IsRefreshing);
        Assert.Equal(taskId, viewModel.SelectedTask?.Id);
        Assert.Equal("MovingToPickup", viewModel.SelectedTask?.Status);
        Assert.Single(viewModel.Tasks);
        Assert.Single(viewModel.Agvs);
        Assert.Equal(taskId, viewModel.SelectedAgv?.MesTransportTaskId);
        Assert.Equal(operationId, viewModel.SelectedAgv?.CurrentTaskId);

        refreshRelease.SetResult(true);
        await refreshTask;

        Assert.False(viewModel.IsRefreshing);
        Assert.Equal(taskId, viewModel.SelectedTask?.Id);
        Assert.Equal(taskId, viewModel.SelectedAgv?.MesTransportTaskId);
    }

    [Fact]
    public async Task Successful_refresh_keeps_resume_enabled_for_a_correlated_paused_task()
    {
        var taskId = Guid.NewGuid();
        var operationId = TransportOperationIds.Pickup(taskId);
        var task = new DashboardTask(
            taskId,
            2,
            4,
            "Paused",
            0,
            null,
            ActiveAgvId: "AGV-01",
            ActiveDeviceTaskId: "device-pickup",
            ActivePath: ["SAMPLE_01", "ST_PREP_01"]);
        var client = new FakeMesClient([task])
        {
            FleetStatus =
            [
                new AgvFleetDashboardStatus(
                    new AgvDashboardSnapshot(true, "adapter", "SAMPLE_01", operationId),
                    new AgvActiveTaskStatus(
                        taskId,
                        operationId,
                        "Paused",
                        "device-pickup",
                        "paused",
                        "ST_PREP_01",
                        null,
                        ["SAMPLE_01", "ST_PREP_01"]))
            ]
        };
        using var viewModel = new MainViewModel(client);

        await viewModel.RefreshAsync();
        Assert.Equal("Paused", viewModel.SelectedTask?.Status);
        Assert.True(viewModel.ResumeAgvCommand.CanExecute(null));
        Assert.False(viewModel.PauseAgvCommand.CanExecute(null));

        var resumeStates = new List<bool>();
        viewModel.ResumeAgvCommand.CanExecuteChanged += (_, _) =>
            resumeStates.Add(viewModel.ResumeAgvCommand.CanExecute(null));
        await viewModel.RefreshAgvAsync();

        Assert.Equal("Paused", viewModel.SelectedTask?.Status);
        Assert.Equal("paused", viewModel.SelectedAgv?.DeviceState);
        Assert.True(viewModel.ResumeAgvCommand.CanExecute(null));
        Assert.Contains(false, resumeStates);
        Assert.True(resumeStates[^1]);

        resumeStates.Clear();
        await viewModel.RefreshAsync();
        Assert.True(viewModel.ResumeAgvCommand.CanExecute(null));
        Assert.Contains(false, resumeStates);
        Assert.True(resumeStates[^1]);
    }

    [Fact]
    public async Task Selecting_a_different_task_notifies_all_agv_commands_with_the_new_task_state()
    {
        var completedTask = new DashboardTask(Guid.NewGuid(), 2, 4, "Completed", 0, null);
        var pausedTaskId = Guid.NewGuid();
        var operationId = TransportOperationIds.Pickup(pausedTaskId);
        var pausedTask = new DashboardTask(
            pausedTaskId,
            2,
            4,
            "Paused",
            0,
            null,
            ActiveAgvId: "AGV-01",
            ActiveDeviceTaskId: "device-pickup",
            ActivePath: ["SAMPLE_01", "ST_PREP_01"]);
        var client = new FakeMesClient([completedTask, pausedTask])
        {
            FleetStatus =
            [
                new AgvFleetDashboardStatus(
                    new AgvDashboardSnapshot(true, "adapter", "SAMPLE_01", operationId),
                    new AgvActiveTaskStatus(
                        pausedTaskId,
                        operationId,
                        "Paused",
                        "device-pickup",
                        "paused",
                        "ST_PREP_01",
                        null,
                        ["SAMPLE_01", "ST_PREP_01"]))
            ]
        };
        using var viewModel = new MainViewModel(client)
        {
            OperatorName = "selection-operator"
        };

        await viewModel.RefreshAsync();
        Assert.Equal(completedTask.Id, viewModel.SelectedTask?.Id);
        Assert.False(viewModel.PauseAgvCommand.CanExecute(null));
        Assert.False(viewModel.ResumeAgvCommand.CanExecute(null));
        Assert.False(viewModel.CancelAgvCommand.CanExecute(null));

        var pauseNotifications = 0;
        var resumeNotifications = 0;
        var cancelNotifications = 0;
        viewModel.PauseAgvCommand.CanExecuteChanged += (_, _) => pauseNotifications++;
        viewModel.ResumeAgvCommand.CanExecuteChanged += (_, _) => resumeNotifications++;
        viewModel.CancelAgvCommand.CanExecuteChanged += (_, _) => cancelNotifications++;

        viewModel.SelectedTask = viewModel.Tasks.Single(task => task.Id == pausedTaskId);

        Assert.True(pauseNotifications > 0);
        Assert.True(resumeNotifications > 0);
        Assert.True(cancelNotifications > 0);
        Assert.False(viewModel.PauseAgvCommand.CanExecute(null));
        Assert.True(viewModel.ResumeAgvCommand.CanExecute(null));
        Assert.True(viewModel.CancelAgvCommand.CanExecute(null));
    }

    [Fact]
    public async Task Changing_operator_name_notifies_the_agv_cancel_command()
    {
        var taskId = Guid.NewGuid();
        var operationId = TransportOperationIds.Pickup(taskId);
        var task = new DashboardTask(
            taskId,
            2,
            4,
            "Paused",
            0,
            null,
            ActiveAgvId: "AGV-01",
            ActiveDeviceTaskId: "device-pickup",
            ActivePath: ["SAMPLE_01", "ST_PREP_01"]);
        var client = new FakeMesClient([task])
        {
            FleetStatus =
            [
                new AgvFleetDashboardStatus(
                    new AgvDashboardSnapshot(true, "adapter", "SAMPLE_01", operationId),
                    new AgvActiveTaskStatus(
                        taskId,
                        operationId,
                        "Paused",
                        "device-pickup",
                        "paused",
                        "ST_PREP_01",
                        null,
                        ["SAMPLE_01", "ST_PREP_01"]))
            ]
        };
        using var viewModel = new MainViewModel(client)
        {
            OperatorName = "cancel-operator"
        };

        await viewModel.RefreshAsync();
        Assert.True(viewModel.CancelAgvCommand.CanExecute(null));

        var cancelStates = new List<bool>();
        viewModel.CancelAgvCommand.CanExecuteChanged += (_, _) =>
            cancelStates.Add(viewModel.CancelAgvCommand.CanExecute(null));

        viewModel.OperatorName = "   ";

        Assert.NotEmpty(cancelStates);
        Assert.False(cancelStates[^1]);
        Assert.False(viewModel.CancelAgvCommand.CanExecute(null));

        cancelStates.Clear();
        viewModel.OperatorName = "restored-operator";

        Assert.NotEmpty(cancelStates);
        Assert.True(cancelStates[^1]);
        Assert.True(viewModel.CancelAgvCommand.CanExecute(null));
    }

    [Fact]
    public async Task Dispose_during_a_blocked_refresh_allows_the_refresh_task_to_complete()
    {
        var refreshEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeMesClient([new DashboardTask(Guid.NewGuid(), 2, 4, "Created", 0, null)])
        {
            TaskSnapshotEntered = refreshEntered,
            TaskSnapshotGate = refreshRelease
        };
        var viewModel = new MainViewModel(client);

        var refreshTask = viewModel.RefreshAsync();
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(viewModel.IsRefreshing);

        viewModel.Dispose();
        refreshRelease.TrySetResult(true);

        await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(refreshTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Selected_agv_pause_command_is_sent_through_mes_to_adapter()
    {
        var operationId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var client = new FakeMesClient([
            new DashboardTask(taskId, 2, 4, "MovingToPickup", 0, null, ActiveAgvId: "AGV-01", ActiveDeviceTaskId: operationId.ToString("N"))
        ])
        {
            FleetStatus = [new AgvFleetDashboardStatus(
                new AgvDashboardSnapshot(true, "adapter", "SAMPLE_01", operationId),
                new AgvActiveTaskStatus(
                    taskId,
                    operationId,
                    "MovingToPickup",
                    operationId.ToString("N"),
                    "moving",
                    "ST_PREP_01",
                    null,
                    ["SAMPLE_01", "ST_PREP_01"]))],
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
        var taskId = Guid.NewGuid();
        var client = new FakeMesClient([
            new DashboardTask(taskId, 2, 4, "MovingToPickup", 0, null, ActiveAgvId: "AGV-01", ActiveDeviceTaskId: operationId.ToString("N"))
        ])
        {
            FleetStatus = [new AgvFleetDashboardStatus(
                new AgvDashboardSnapshot(true, "adapter", "SAMPLE_01", operationId),
                new AgvActiveTaskStatus(
                    taskId,
                    operationId,
                    "MovingToPickup",
                    operationId.ToString("N"),
                    "moving",
                    "ST_PREP_01",
                    null,
                    ["SAMPLE_01", "ST_PREP_01"]))],
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

    [Fact]
    public async Task Physical_dispatch_fails_closed_when_readiness_evidence_is_missing()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "Created", 0, null);
        var client = new FakeMesClient([task])
        {
            RuntimeReadiness = null,
            PhysicalPreflight = null
        };
        using var viewModel = new MainViewModel(client);

        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsDispatchReadinessSatisfied);
        Assert.False(viewModel.DispatchTaskCommand.CanExecute(null));
        Assert.Contains("NO-GO", viewModel.PhysicalPreflightStatus, StringComparison.Ordinal);
        Assert.Contains("证据缺失", viewModel.PhysicalPreflightStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Physical_dispatch_rechecks_manual_block_and_map_evidence()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "Created", 0, null);
        var client = new FakeMesClient([task]);
        var baseline = client.PhysicalPreflight!;
        client.PhysicalPreflight = baseline with
        {
            Readiness = baseline.Readiness! with
            {
                ManualBlock = true,
                MapMd5 = "unexpected-md5"
            },
            DispatchPermitted = true,
            BlockingReasons = []
        };
        using var viewModel = new MainViewModel(client);

        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsDispatchReadinessSatisfied);
        Assert.False(viewModel.DispatchTaskCommand.CanExecute(null));
        Assert.Contains("manualBlock", viewModel.PhysicalPreflightStatus, StringComparison.Ordinal);
        Assert.Contains("MD5", viewModel.PhysicalPreflightStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Physical_dispatch_is_enabled_only_with_matching_go_evidence()
    {
        var task = new DashboardTask(Guid.NewGuid(), 2, 4, "Created", 0, null);
        var client = new FakeMesClient([task]);
        using var viewModel = new MainViewModel(client);

        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsDispatchReadinessSatisfied);
        Assert.True(viewModel.DispatchTaskCommand.CanExecute(null));
        Assert.Contains("GO", viewModel.PhysicalPreflightStatus, StringComparison.Ordinal);
        Assert.Contains(client.RuntimeReadiness!.ProfileFingerprint, viewModel.RuntimeReadinessStatus, StringComparison.Ordinal);
        Assert.Contains(client.RuntimeReadiness.MapFingerprint, viewModel.MapReadinessStatus, StringComparison.Ordinal);
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
    public TaskCompletionSource<bool>? TaskSnapshotEntered { get; set; }
    public TaskCompletionSource<bool>? TaskSnapshotGate { get; set; }
    public IReadOnlyList<AgvFleetDashboardStatus>? FleetStatus { get; set; }
    public AgvCommandResult? CommandResult { get; set; }
    public int AgvCommandCallCount { get; private set; }
    public (string AgvId, string Command, Guid? TaskId)? LastAgvCommand { get; private set; }
    public DashboardPlannedPath? PlannedPath { get; set; }
    public RuntimeReadinessResponse? RuntimeReadiness { get; set; } = CreateRuntimeReadiness();
    public PhysicalAgvPreflightResponse? PhysicalPreflight { get; set; } = CreatePhysicalPreflight();
    public (string FromStationId, string ToStationId, IReadOnlyCollection<string>? BlockedStations)? LastPlanRequest { get; private set; }
    public (int SourceStationCode, int TargetStationCode, int Priority, string? Description, string? ExternalId)? LastCreateRequest { get; private set; }
    public IReadOnlyList<DashboardStation> Stations => _stations;

    public Task<RuntimeReadinessResponse?> GetRuntimeReadinessAsync(CancellationToken cancellationToken) =>
        Task.FromResult(RuntimeReadiness);
    public Task<PhysicalAgvPreflightResponse?> GetPhysicalPreflightAsync(CancellationToken cancellationToken) =>
        Task.FromResult(PhysicalPreflight);

    public void SetStations(params DashboardStation[] stations)
    {
        _stations.Clear();
        _stations.AddRange(stations);
    }

    public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken) => Task.FromResult(CurrentTasks());
    public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(DateOnly date, CancellationToken cancellationToken)
    {
        LastRequestedDate = date;
        return GetTaskSnapshotAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<DashboardTask>> GetTaskSnapshotAsync(CancellationToken cancellationToken)
    {
        TaskSnapshotEntered?.TrySetResult(true);
        if (TaskSnapshotGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return CurrentTasks();
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

    private static RuntimeReadinessResponse CreateRuntimeReadiness() => new(
        "TEST-PROFILE",
        "Test AGV MES",
        "1.0",
        UseSimulator: false,
        AutomaticDispatchEnabled: true,
        TaskCancellationEnabled: true,
        ProfileFingerprint: new string('a', 64),
        MapFingerprint: new string('b', 64),
        StationIds: ["SAMPLE_01", "ST_PREP_01"],
        DirectedEdges: [new DirectedMapEdgeResponse("SAMPLE_01", "ST_PREP_01", 1)],
        ExpectedPhysicalMapName: "test-map",
        ExpectedPhysicalMapVersion: "v1",
        ExpectedPhysicalMapMd5: "expected-md5");

    private static PhysicalAgvPreflightResponse CreatePhysicalPreflight()
    {
        var readiness = new AgvSafetyReadinessResponse(
            "automatic",
            "test",
            "test-map",
            "expected-md5",
            ForkAutomatic: true,
            DispatchMode: 1,
            ManualBlock: false,
            SrcRelease: true,
            Emergency: false,
            Blocked: false,
            FatalCount: 0,
            ErrorCount: 0,
            RelocationStatus: 1,
            LocalizationConfidence: 1,
            ObservedAtUtc: DateTimeOffset.UtcNow);
        return new PhysicalAgvPreflightResponse(
            new AgvSnapshotResponse(
                true,
                "adapter",
                "SAMPLE_01",
                null,
                "AGV-01",
                SafetyReadiness: readiness),
            readiness,
            DispatchPermitted: true,
            BlockingReasons: []);
    }
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
