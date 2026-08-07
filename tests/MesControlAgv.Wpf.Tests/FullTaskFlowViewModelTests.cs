using MesControlAgv.Domain;
using MesControlAgv.Wpf.Services;
using MesControlAgv.Wpf.ViewModels;

namespace MesControlAgv.Wpf.Tests;

public sealed class FullTaskFlowViewModelTests
{
    [Fact]
    public async Task Configured_task_runs_through_dispatch_pause_resume_arrival_and_completion()
    {
        var client = new StatefulFlowMesClient();
        var simulator = new RecordingFlowSimulatorClient();
        using var viewModel = new MainViewModel(client, simulator);

        await viewModel.RefreshAsync();

        viewModel.NewTaskSourceStation = client.Stations.Single(station => station.Code == 2);
        viewModel.NewTaskTargetStation = client.Stations.Single(station => station.Code == 4);
        viewModel.NewTaskPriority = 7;
        viewModel.NewTaskDescription = "WPF full flow";
        viewModel.NewTaskExternalId = "WPF-FLOW-01";
        viewModel.PlanRouteCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.PlannedRoute is not null);

        Assert.True(viewModel.CreateTaskCommand.CanExecute(null));
        viewModel.CreateTaskCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedTask?.Status == "Created");

        var taskId = client.CreatedTaskId;
        Assert.Equal((2, 4, 7, "WPF full flow", "WPF-FLOW-01"), client.LastCreateRequest);
        Assert.Single(viewModel.Tasks);
        Assert.Equal(taskId, viewModel.SelectedTask?.Id);
        Assert.Equal("Created", viewModel.SelectedTask?.Status);
        Assert.Equal("无 MES 活动任务", viewModel.SelectedAgv?.MesTaskStatus);

        Assert.True(viewModel.DispatchTaskCommand.CanExecute(null));
        viewModel.DispatchTaskCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedTask?.Status == "MovingToPickup");

        var pickupOperationId = TransportOperationIds.Pickup(taskId);
        Assert.Equal(taskId, viewModel.SelectedTask?.Id);
        Assert.Equal("AGV-01", viewModel.SelectedTask?.AssignedAgvDescription);
        Assert.Equal("device-pickup", viewModel.SelectedTask?.DeviceTaskDescription);
        Assert.Equal("MovingToPickup", viewModel.SelectedAgv?.MesTaskStatus);
        Assert.Equal("moving", viewModel.SelectedAgv?.DeviceState);
        Assert.Equal(pickupOperationId, viewModel.SelectedAgv?.CurrentTaskId);
        Assert.Contains("SAMPLE_01", viewModel.SelectedAgv?.ExecutionPath, StringComparison.Ordinal);

        Assert.True(viewModel.PauseAgvCommand.CanExecute(null));
        viewModel.PauseAgvCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedTask?.Status == "Paused");
        Assert.Equal("Paused", viewModel.SelectedAgv?.MesTaskStatus);
        Assert.Equal("paused", viewModel.SelectedAgv?.DeviceState);
        Assert.Equal(pickupOperationId, client.LastAgvCommand?.TaskId);
        Assert.Equal(("AGV-01", "pause", pickupOperationId), client.LastAgvCommand);

        Assert.True(viewModel.ResumeAgvCommand.CanExecute(null));
        viewModel.ResumeAgvCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedTask?.Status == "MovingToPickup");
        Assert.Equal("MovingToPickup", viewModel.SelectedAgv?.MesTaskStatus);
        Assert.Equal("moving", viewModel.SelectedAgv?.DeviceState);
        Assert.Equal(("AGV-01", "resume", pickupOperationId), client.LastAgvCommand);

        // Release builds intentionally hide simulator arrival controls. The
        // complete simulated loop is exercised by Debug; Release must prove
        // that the same command cannot bypass the physical-mode safety gate.
        if (!viewModel.IsManualArrivalAvailable)
        {
            Assert.False(viewModel.ArriveCommand.CanExecute(null));
            Assert.Equal("MovingToPickup", viewModel.SelectedTask?.Status);
            return;
        }

        viewModel.ArriveCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedTask?.Status == "WaitingPickupConfirmation");
        Assert.Equal(pickupOperationId, simulator.LastTaskControlId);
        Assert.Equal("arrive", simulator.LastMode);
        Assert.Equal("WaitingPickupConfirmation", viewModel.SelectedTask?.Status);
        Assert.Equal("arrived", viewModel.SelectedAgv?.DeviceState);
        Assert.True(viewModel.ConfirmPickupCommand.CanExecute(null));

        viewModel.OperatorName = "operator-flow";
        viewModel.ConfirmPickupCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedTask?.Status == "MovingToDropoff");

        var dropoffOperationId = TransportOperationIds.Dropoff(taskId);
        Assert.Equal("MovingToDropoff", viewModel.SelectedTask?.Status);
        Assert.Equal("MovingToDropoff", viewModel.SelectedAgv?.MesTaskStatus);
        Assert.Equal("moving", viewModel.SelectedAgv?.DeviceState);
        Assert.Equal(dropoffOperationId, viewModel.SelectedAgv?.CurrentTaskId);
        Assert.Equal("device-dropoff", viewModel.SelectedTask?.DeviceTaskDescription);
        Assert.Equal("operator-flow", client.LastPickupOperator);

        viewModel.ArriveCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedTask?.Status == "WaitingDropoffConfirmation");
        Assert.Equal(dropoffOperationId, simulator.LastTaskControlId);
        Assert.Equal("WaitingDropoffConfirmation", viewModel.SelectedTask?.Status);
        Assert.Equal("arrived", viewModel.SelectedAgv?.DeviceState);
        Assert.True(viewModel.ConfirmDropoffCommand.CanExecute(null));

        viewModel.ConfirmDropoffCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedTask?.Status == "Completed");

        Assert.Single(viewModel.Tasks);
        Assert.Equal(taskId, viewModel.SelectedTask?.Id);
        Assert.Equal("Completed", viewModel.Tasks[0].Status);
        Assert.Equal("无 MES 活动任务", viewModel.SelectedAgv?.MesTaskStatus);
        Assert.Equal("-", viewModel.SelectedAgv?.DeviceState);
        Assert.Null(viewModel.SelectedAgv?.CurrentTaskId);
        Assert.Equal("operator-flow", client.LastDropoffOperator);
        Assert.Equal(2, client.MarkArrivedCallCount);
        Assert.Equal(2, simulator.ArrivalCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("The expected asynchronous command did not complete.");
    }
}

internal sealed class StatefulFlowMesClient : IMesClient
{
    private readonly List<DashboardStation> _stations =
    [
        new DashboardStation(2, "Sample", "SAMPLE_01", true),
        new DashboardStation(4, "Preparation", "ST_PREP_01", true)
    ];

    private DashboardTask? _task;
    private string _currentStationId = "ST_OPEN_01";
    private string? _deviceState;

    public IReadOnlyList<DashboardStation> Stations => _stations;
    public Guid CreatedTaskId => _task?.Id ?? throw new InvalidOperationException("The task has not been created.");
    public (int SourceStationCode, int TargetStationCode, int Priority, string? Description, string? ExternalId)? LastCreateRequest { get; private set; }
    public (string AgvId, string Command, Guid? TaskId)? LastAgvCommand { get; private set; }
    public string? LastPickupOperator { get; private set; }
    public string? LastDropoffOperator { get; private set; }
    public int MarkArrivedCallCount { get; private set; }

    public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DashboardTask>>(CurrentTasks());

    public Task<IReadOnlyList<DashboardTask>> GetTasksAsync(DateOnly date, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DashboardTask>>(CurrentTasks());

    public Task<KpiDashboard> GetKpiDashboardAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var current = CurrentTasks();
        var completed = current.Count(task => task.Status == "Completed");
        var failed = current.Count(task => task.Status == "Failed");
        var cancelled = current.Count(task => task.Status == "Cancelled");
        var running = current.Count - completed - failed - cancelled;
        return Task.FromResult(new KpiDashboard(
            date,
            new KpiTaskSummary(current.Count, running, completed, failed, cancelled),
            [],
            new KpiSampleSummary(0, 0, 0, 0, 0, 0, "stateful WPF test"),
            [],
            []));
    }

    public Task<DashboardTaskDetail?> GetTaskDetailAsync(Guid taskId, CancellationToken cancellationToken)
    {
        if (_task is not { } task || task.Id != taskId) return Task.FromResult<DashboardTaskDetail?>(null);
        return Task.FromResult<DashboardTaskDetail?>(new DashboardTaskDetail(task, []));
    }

    public Task<AgvDashboardSnapshot> GetAgvSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromResult(BuildFleetStatus().Snapshot);

    public Task<IReadOnlyList<AgvFleetDashboardStatus>> GetAgvFleetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgvFleetDashboardStatus>>([BuildFleetStatus()]);

    public Task<IReadOnlyList<DashboardStation>> GetStationsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DashboardStation>>(_stations);

    public Task<DashboardPlannedPath> PlanPathAsync(
        string fromStationId,
        string toStationId,
        IReadOnlyCollection<string>? blockedStations,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DashboardPlannedPath([fromStationId, toStationId], 1));

    public Task<DashboardTask> CreateTaskAsync(CancellationToken cancellationToken) =>
        CreateTaskAsync(2, 4, 0, null, null, cancellationToken);

    public Task<DashboardTask> CreateTaskAsync(
        int sourceStationCode,
        int targetStationCode,
        int priority,
        string? description,
        string? externalId,
        CancellationToken cancellationToken)
    {
        LastCreateRequest = (sourceStationCode, targetStationCode, priority, description, externalId);
        _task = new DashboardTask(
            Guid.NewGuid(),
            sourceStationCode,
            targetStationCode,
            "Created",
            0,
            null,
            priority,
            description,
            externalId,
            DateTime.UtcNow);
        _currentStationId = "ST_OPEN_01";
        _deviceState = null;
        return Task.FromResult(_task);
    }

    public Task<DashboardTask> DispatchTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        EnsureTask(taskId, "Created");
        _task = _task! with
        {
            Status = "MovingToPickup",
            ActiveAgvId = "AGV-01",
            ActiveDeviceTaskId = "device-pickup",
            ActivePath = ["ST_OPEN_01", "SAMPLE_01"]
        };
        _deviceState = "moving";
        return Task.FromResult(_task);
    }

    public Task<AgvCommandResult?> ExecuteAgvCommandAsync(
        string agvId,
        string command,
        Guid? taskId,
        CancellationToken cancellationToken)
    {
        if (agvId != "AGV-01" || taskId is not { } operationId) throw new InvalidOperationException("Unexpected AGV command.");
        var expected = ActiveOperationId();
        if (operationId != expected) throw new InvalidOperationException("The command operation does not match the active task.");

        LastAgvCommand = (agvId, command, taskId);
        switch (command)
        {
            case "pause":
                if (_task?.Status is not ("MovingToPickup" or "MovingToDropoff")) throw new InvalidOperationException("Only a moving task can be paused.");
                _task = _task with { Status = "Paused" };
                _deviceState = "paused";
                break;
            case "resume":
                if (_task?.Status != "Paused") throw new InvalidOperationException("Only a paused task can be resumed.");
                _task = _task with { Status = IsDropoffOperation(operationId) ? "MovingToDropoff" : "MovingToPickup" };
                _deviceState = "moving";
                break;
            default:
                throw new InvalidOperationException($"Unsupported command: {command}");
        }

        var target = ActiveTargetStationId();
        return Task.FromResult<AgvCommandResult?>(new AgvCommandResult(
            operationId,
            _task.ActiveDeviceTaskId ?? string.Empty,
            target,
            _deviceState,
            null,
            "AGV-01",
            _task.ActivePath));
    }

    public Task<DashboardTask> MarkArrivedAsync(Guid taskId, CancellationToken cancellationToken)
    {
        EnsureTask(taskId, "MovingToPickup", "MovingToDropoff");
        MarkArrivedCallCount++;
        var arrivedAtPickup = _task!.Status == "MovingToPickup";
        _task = _task with { Status = arrivedAtPickup ? "WaitingPickupConfirmation" : "WaitingDropoffConfirmation" };
        _currentStationId = ActiveTargetStationId();
        _deviceState = "arrived";
        return Task.FromResult(_task);
    }

    public Task<DashboardTask> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken)
    {
        EnsureTask(taskId, "WaitingPickupConfirmation");
        LastPickupOperator = operatorName;
        _task = _task! with
        {
            Status = "MovingToDropoff",
            ActiveDeviceTaskId = "device-dropoff",
            ActivePath = ["SAMPLE_01", "ST_PREP_01"]
        };
        _deviceState = "moving";
        return Task.FromResult(_task);
    }

    public Task<DashboardTask> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken)
    {
        EnsureTask(taskId, "WaitingDropoffConfirmation");
        LastDropoffOperator = operatorName;
        _task = _task! with { Status = "Completed", EndedAt = DateTime.UtcNow };
        _currentStationId = "ST_PREP_01";
        _deviceState = null;
        return Task.FromResult(_task);
    }

    public Task<DashboardTask> RetryAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(EnsureTask(taskId));
    public Task<DashboardTask> RecoverAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(EnsureTask(taskId));
    public Task<DashboardTask> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) => Task.FromResult(EnsureTask(taskId));

    private IReadOnlyList<DashboardTask> CurrentTasks() => _task is { } task ? [task] : [];

    private AgvFleetDashboardStatus BuildFleetStatus()
    {
        var task = _task;
        if (task is null || task.Status is "Created" or "Completed" or "Cancelled")
        {
            return new AgvFleetDashboardStatus(
                new AgvDashboardSnapshot(true, "simulator", _currentStationId, null),
                null);
        }

        var operationId = ActiveOperationId();
        var active = new AgvActiveTaskStatus(
            task.Id,
            operationId,
            task.Status,
            task.ActiveDeviceTaskId,
            _deviceState,
            ActiveTargetStationId(),
            task.LastError,
            task.ActivePath);
        return new AgvFleetDashboardStatus(
            new AgvDashboardSnapshot(true, "simulator", _currentStationId, operationId),
            active);
    }

    private Guid ActiveOperationId()
    {
        if (_task is null) throw new InvalidOperationException("The task has not been created.");
        return IsDropoffOperationForStatus(_task.Status)
            ? TransportOperationIds.Dropoff(_task.Id)
            : TransportOperationIds.Pickup(_task.Id);
    }

    private string ActiveTargetStationId()
    {
        if (_task is null) throw new InvalidOperationException("The task has not been created.");
        var stationCode = IsDropoffOperationForStatus(_task.Status) ? _task.TargetStationCode : _task.SourceStationCode;
        return _stations.Single(station => station.Code == stationCode).AgvStationId;
    }

    private static bool IsDropoffOperationForStatus(string status) =>
        status is "MovingToDropoff" or "WaitingDropoffConfirmation";

    private bool IsDropoffOperation(Guid operationId) =>
        _task is not null && operationId == TransportOperationIds.Dropoff(_task.Id);

    private DashboardTask EnsureTask(Guid taskId, params string[] expectedStatuses)
    {
        if (_task is not { } task || task.Id != taskId)
            throw new InvalidOperationException("The requested task does not exist.");
        if (expectedStatuses.Length > 0 && !expectedStatuses.Contains(task.Status, StringComparer.Ordinal))
            throw new InvalidOperationException($"Unexpected task status: {task.Status}");
        return task;
    }
}

internal sealed class RecordingFlowSimulatorClient : ISimulatorControlClient
{
    public int ArrivalCount { get; private set; }
    public Guid? LastTaskControlId { get; private set; }
    public string? LastMode { get; private set; }

    public Task ApplyControlAsync(string mode, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ApplyControlAsync(Guid deviceTaskId, string mode, CancellationToken cancellationToken)
    {
        ArrivalCount++;
        LastTaskControlId = deviceTaskId;
        LastMode = mode;
        return Task.CompletedTask;
    }
}
