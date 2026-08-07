using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Domain;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Modules;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IMesClient _mes;
    private readonly ISimulatorControlClient? _simulator;
    private readonly ControlCenterViewModel _modules;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(2));
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private CancellationTokenSource? _detailRefresh;
    private Task? _refreshLoop;
    private TaskRowViewModel? _selectedTask;
    private AgvRowViewModel? _selectedAgv;
    private bool _suppressDetailRefresh;
    private string _connectionStatus = "\u6B63\u5728\u8FDE\u63A5 MES";
    private string _agvStatus = "\u672A\u77E5";
    private string _agvStation = "-";
    private string _agvExecutionStatus = "\u65E0\u6D3B\u52A8\u8FD0\u8F93\u4EFB\u52A1";
    private string _message = string.Empty;
    private string _actionStatus = "\u8BF7\u521B\u5EFA\u4EFB\u52A1\uFF0C\u7136\u540E\u4ECE\u4EFB\u52A1\u5217\u8868\u4E2D\u663E\u5F0F\u6D3E\u53D1\u3002";
    private string _batchStatus = "\u8BF7\u9009\u62E9 CSV \u6216 XLSX \u6587\u4EF6\u5BFC\u5165\u4EFB\u52A1";
    private DateTime? _taskFilterDate = DateTime.UtcNow.Date;
    private DashboardStation? _newTaskSourceStation;
    private DashboardStation? _newTaskTargetStation;
    private int _newTaskPriority;
    private string _newTaskDescription = string.Empty;
    private string _newTaskExternalId = string.Empty;
    private string _operatorName = Environment.UserName;
    private DashboardPlannedPath? _plannedRoute;
    private string _routePreview = "\u8BF7\u9009\u62E9\u8D77\u70B9\u548C\u7EC8\u70B9\u540E\u9884\u89C8\u8DEF\u7EBF\u3002";
    private IReadOnlyList<DashboardStation> _stationCatalog = [];
    private bool _isRefreshing;
    private bool _isDataStale = true;
    private DateTimeOffset? _lastRefreshAt;
    private bool _isActionInProgress;
    private string _currentAction = string.Empty;

    public MainViewModel(IMesClient mes, ISimulatorControlClient? simulator = null, ControlCenterModuleRegistry? moduleRegistry = null)
    {
        _mes = mes;
        _simulator = simulator;
        ModuleRegistry = moduleRegistry ?? ControlCenterModuleRegistry.CreateStandard();
        WorkflowEditor = new WorkflowEditorViewModel(new WorkflowStore(), _mes, () => OperatorName);
        _modules = new ControlCenterViewModel(WorkflowEditor, ModuleRegistry);
        Kpi = _modules.KpiDashboard;
        CreateTaskCommand = CreateActionCommand("\u521B\u5EFA\u4EFB\u52A1", CreateTaskAsync, CanCreateTask);
        DispatchTaskCommand = CreateActionCommand("\u6D3E\u53D1\u4EFB\u52A1", DispatchTaskAsync, CanDispatchTask);
        PlanRouteCommand = CreateActionCommand("\u9884\u89C8\u8DEF\u7EBF", PlanRouteAsync, CanPlanRoute);
        ArriveCommand = CreateActionCommand("\u4EFF\u771F\u5230\u7AD9", ArriveAsync, CanApplyManualArrival);
        ConfirmPickupCommand = CreateActionCommand("\u786E\u8BA4\u53D6\u8D27", ConfirmPickupAsync, () => HasOperator && SelectedTask?.Status == "WaitingPickupConfirmation");
        ConfirmDropoffCommand = CreateActionCommand("\u786E\u8BA4\u653E\u8D27", ConfirmDropoffAsync, () => HasOperator && SelectedTask?.Status == "WaitingDropoffConfirmation");
        RetryCommand = CreateActionCommand("\u91CD\u8BD5\u4EFB\u52A1", RetryAsync, () => SelectedTask?.Status == "Failed");
        RecoverCommand = CreateActionCommand("\u6062\u590D\u4EFB\u52A1", RecoverAsync, () => SelectedTask?.Status == "Unknown");
        CancelCommand = CreateActionCommand("\u53D6\u6D88\u4EFB\u52A1", CancelAsync, () => HasOperator && SelectedTask is { Status: not "Completed" and not "Cancelled" });
        SimulatorArriveCommand = CreateActionCommand("\u4EFF\u771F\u5230\u7AD9", () => ApplySimulatorControlAsync("arrive"), () => IsSimulatorPanelVisible);
        SimulatorFailCommand = CreateActionCommand("\u6A21\u62DF\u5931\u8D25", () => ApplySimulatorControlAsync("fail"), () => IsSimulatorPanelVisible);
        SimulatorTimeoutCommand = CreateActionCommand("\u6A21\u62DF\u8D85\u65F6", () => ApplySimulatorControlAsync("timeout"), () => IsSimulatorPanelVisible);
        SimulatorOfflineCommand = CreateActionCommand("\u6A21\u62DF\u79BB\u7EBF", () => ApplySimulatorControlAsync("offline"), () => IsSimulatorPanelVisible);
        SimulatorRecoverCommand = CreateActionCommand("\u6A21\u62DF\u6062\u590D", () => ApplySimulatorControlAsync("recover"), () => IsSimulatorPanelVisible);
        RefreshAgvCommand = CreateActionCommand("\u5237\u65B0 AGV", RefreshAgvAsync);
        QueryTasksCommand = CreateActionCommand("\u67E5\u8BE2\u4EFB\u52A1", () => RefreshAsync());
        RefreshTasksCommand = CreateActionCommand("\u5237\u65B0\u4EFB\u52A1", () => RefreshAsync());
        PauseAgvCommand = CreateActionCommand("\u6682\u505C AGV", () => ExecuteAgvCommandAsync("pause"), () => CanControlSelectedAgv("pause"));
        ResumeAgvCommand = CreateActionCommand("\u6062\u590D AGV", () => ExecuteAgvCommandAsync("resume"), () => CanControlSelectedAgv("resume"));
        CancelAgvCommand = CreateActionCommand("\u53D6\u6D88 AGV \u4EFB\u52A1", () => ExecuteAgvCommandAsync("cancel"), () => CanControlSelectedAgv("cancel"));
        SortBatchCommand = CreateActionCommand("\u6392\u5E8F\u6279\u91CF\u4EFB\u52A1", () => { SortBatchTasks(); return Task.CompletedTask; }, () => BatchTasks.Count > 1);
        SubmitBatchCommand = CreateActionCommand("\u63D0\u4EA4\u6279\u91CF\u4EFB\u52A1", SubmitBatchAsync, () => BatchTasks.Any(task => task.Status == "\u5F85\u63D0\u4EA4"));
        ClearBatchCommand = CreateActionCommand("\u6E05\u7A7A\u6279\u91CF\u4EFB\u52A1", () =>
        {
            _modules.BatchImport.Clear();
            BatchStatus = _modules.BatchImport.BatchStatus;
            RefreshBatchCommandState();
            return Task.CompletedTask;
        });
    }

    public ObservableCollection<TaskRowViewModel> Tasks => _modules.TaskMonitor.Tasks;
    public ObservableCollection<TaskEventRowViewModel> TaskEvents => _modules.TaskMonitor.TaskEvents;
    public ObservableCollection<AgvRowViewModel> Agvs => _modules.AgvCommunication.Agvs;
    public ObservableCollection<BatchTaskRowViewModel> BatchTasks => _modules.BatchImport.BatchTasks;
    public ObservableCollection<string> BatchImportIssues => _modules.BatchImport.BatchImportIssues;
    public ObservableCollection<DashboardStation> AvailableStations { get; } = [];
    public WorkflowEditorViewModel WorkflowEditor { get; }
    public KpiDashboardViewModel Kpi { get; }
    public ControlCenterModuleRegistry ModuleRegistry { get; }
    public ControlCenterViewModel Modules => _modules;
    public TaskMonitorViewModel TaskMonitor => _modules.TaskMonitor;
    public AgvCommunicationViewModel AgvCommunication => _modules.AgvCommunication;
    public BatchImportViewModel BatchImport => _modules.BatchImport;

    public TaskRowViewModel? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (!SetField(ref _selectedTask, value)) return;
            _modules.TaskMonitor.SelectedTask = value;
            RefreshCommandState();
            if (!_suppressDetailRefresh) RequestTaskDetailRefresh();
        }
    }

    public AgvRowViewModel? SelectedAgv
    {
        get => _selectedAgv;
        set
        {
            if (!SetField(ref _selectedAgv, value)) return;
            _modules.AgvCommunication.SelectedAgv = value;
            RefreshAgvCommandState();
        }
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set
        {
            if (!SetField(ref _connectionStatus, value)) return;
            _modules.TaskMonitor.ConnectionStatus = value;
        }
    }
    public string AgvStatus
    {
        get => _agvStatus;
        private set
        {
            if (!SetField(ref _agvStatus, value)) return;
            _modules.AgvCommunication.AgvStatus = value;
        }
    }
    public string AgvStation
    {
        get => _agvStation;
        private set
        {
            if (!SetField(ref _agvStation, value)) return;
            _modules.AgvCommunication.AgvStation = value;
        }
    }
    public string Message { get => _message; private set => SetField(ref _message, value); }
    public string ActionStatus { get => _actionStatus; private set => SetField(ref _actionStatus, value); }
    public bool IsActionInProgress
    {
        get => _isActionInProgress;
        private set
        {
            if (!SetField(ref _isActionInProgress, value)) return;
            RefreshAllCommandState();
        }
    }
    public string CurrentAction
    {
        get => _currentAction;
        private set => SetField(ref _currentAction, value);
    }
    public DashboardStation? NewTaskSourceStation
    {
        get => _newTaskSourceStation;
        set
        {
            if (!SetField(ref _newTaskSourceStation, value)) return;
            InvalidateRoutePreview();
        }
    }
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (!SetField(ref _isRefreshing, value)) return;
            OnPropertyChanged(nameof(RefreshStatus));
        }
    }
    public bool IsDataStale
    {
        get => _isDataStale;
        private set
        {
            if (!SetField(ref _isDataStale, value)) return;
            OnPropertyChanged(nameof(RefreshStatus));
        }
    }
    public DateTimeOffset? LastRefreshAt
    {
        get => _lastRefreshAt;
        private set
        {
            if (!SetField(ref _lastRefreshAt, value)) return;
            OnPropertyChanged(nameof(RefreshStatus));
        }
    }
    public string RefreshStatus => IsRefreshing
        ? "正在刷新控制中心数据..."
        : IsDataStale
            ? LastRefreshAt is null
                ? "数据尚未成功刷新"
                : $"数据可能已过期，最后成功刷新：{LastRefreshAt.Value.LocalDateTime:HH:mm:ss}"
            : $"数据已更新：{LastRefreshAt?.LocalDateTime:HH:mm:ss}";
    public string AgvExecutionStatus
    {
        get => _agvExecutionStatus;
        private set => SetField(ref _agvExecutionStatus, value);
    }
    public DashboardStation? NewTaskTargetStation
    {
        get => _newTaskTargetStation;
        set
        {
            if (!SetField(ref _newTaskTargetStation, value)) return;
            InvalidateRoutePreview();
        }
    }
    public int NewTaskPriority
    {
        get => _newTaskPriority;
        set
        {
            if (!SetField(ref _newTaskPriority, value)) return;
            RefreshCreateTaskCommandState();
        }
    }
    public string NewTaskDescription
    {
        get => _newTaskDescription;
        set => SetField(ref _newTaskDescription, value ?? string.Empty);
    }
    public string NewTaskExternalId
    {
        get => _newTaskExternalId;
        set => SetField(ref _newTaskExternalId, value ?? string.Empty);
    }
    public string OperatorName
    {
        get => _operatorName;
        set
        {
            if (!SetField(ref _operatorName, value ?? string.Empty)) return;
            RefreshCommandState();
            OnPropertyChanged(nameof(HasValidOperator));
            OnPropertyChanged(nameof(OperatorValidationMessage));
        }
    }
    public bool HasValidOperator => HasOperator;
    public string OperatorValidationMessage => HasOperator
        ? string.Empty
        : "\u8BF7\u8F93\u5165\u64CD\u4F5C\u5458\u540E\u518D\u786E\u8BA4\u3001\u53D6\u6D88\u4EFB\u52A1\u3002";
    public DashboardPlannedPath? PlannedRoute
    {
        get => _plannedRoute;
        private set => SetField(ref _plannedRoute, value);
    }
    public string RoutePreview
    {
        get => _routePreview;
        private set => SetField(ref _routePreview, value);
    }
    public string TaskFormStatus => GetTaskFormStatus();
    public string BatchStatus
    {
        get => _batchStatus;
        private set
        {
            if (!SetField(ref _batchStatus, value)) return;
            _modules.BatchImport.BatchStatus = value;
        }
    }

    public DateTime? TaskFilterDate
    {
        get => _taskFilterDate;
        set
        {
            if (value is not { } date) return;
            if (SetField(ref _taskFilterDate, date.Date)) _modules.TaskMonitor.TaskFilterDate = date.Date;
        }
    }

    private DateOnly CurrentTaskDate => DateOnly.FromDateTime(TaskFilterDate ?? DateTime.UtcNow.Date);

    public bool IsSimulatorMode => _simulator is not null;
    public bool IsPhysicalMode => !IsSimulatorMode;
#if DEBUG
    public bool IsSimulatorPanelVisible => IsSimulatorMode;
#else
    public bool IsSimulatorPanelVisible => false;
#endif
    public bool IsManualArrivalAvailable => IsSimulatorPanelVisible;
    public string RuntimeMode => IsSimulatorMode ? "Simulator" : "Physical / \u5B89\u5168\u6A21\u5F0F";
    public string RuntimeModeDescription => IsSimulatorPanelVisible
        ? "\u5141\u8BB8\u5F00\u53D1\u7528\u5230\u7AD9\u4E0E\u6545\u969C\u6CE8\u5165"
        : IsSimulatorMode
            ? "Release \u5B89\u5168\u67E5\u770B\uFF1A\u4EFF\u771F\u63A7\u5236\u5DF2\u7981\u7528"
            : "\u624B\u5DE5\u5230\u7AD9\u4E0E\u5BFC\u822A\u63A7\u5236\u5DF2\u7981\u7528";

    public ICommand CreateTaskCommand { get; }
    public ICommand DispatchTaskCommand { get; }
    public ICommand PlanRouteCommand { get; }
    public ICommand ArriveCommand { get; }
    public ICommand ConfirmPickupCommand { get; }
    public ICommand ConfirmDropoffCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand RecoverCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SimulatorArriveCommand { get; }
    public ICommand SimulatorFailCommand { get; }
    public ICommand SimulatorTimeoutCommand { get; }
    public ICommand SimulatorOfflineCommand { get; }
    public ICommand SimulatorRecoverCommand { get; }
    public ICommand RefreshAgvCommand { get; }
    public ICommand QueryTasksCommand { get; }
    public ICommand RefreshTasksCommand { get; }
    public ICommand PauseAgvCommand { get; }
    public ICommand ResumeAgvCommand { get; }
    public ICommand CancelAgvCommand { get; }
    public ICommand SortBatchCommand { get; }
    public ICommand SubmitBatchCommand { get; }
    public ICommand ClearBatchCommand { get; }

    public async Task StartAsync()
    {
        await RefreshAsync();
        _refreshLoop = RefreshLoopAsync(_shutdown.Token);
    }

    public async Task RefreshAsync(Guid? preferredTaskId = null)
    {
        if (!await TryEnterRefreshAsync()) return;

        IsRefreshing = true;
        try
        {
            // Refresh the profile catalog with the task/fleet snapshot. A
            // profile reload must invalidate a route preview instead of
            // allowing a task to be created against stale station metadata.
            await LoadStationsAsync();
            var tasks = await _mes.GetTasksAsync(CurrentTaskDate, _shutdown.Token);
            var fleetStatus = await _mes.GetAgvFleetStatusAsync(_shutdown.Token);
            await Kpi.RefreshAsync(_mes, CurrentTaskDate, _shutdown.Token);
            var selectedId = preferredTaskId ?? SelectedTask?.Id;
            Tasks.Clear();
            foreach (var task in tasks) Tasks.Add(TaskRowViewModel.From(task, _stationCatalog));
            CancelPendingDetailRefresh();
            _suppressDetailRefresh = true;
            try
            {
                SelectedTask = preferredTaskId is { } preferredId
                    ? Tasks.SingleOrDefault(task => task.Id == preferredId)
                    : Tasks.SingleOrDefault(task => task.Id == selectedId) ?? Tasks.FirstOrDefault();
            }
            finally { _suppressDetailRefresh = false; }
            await LoadTaskDetailAsync(SelectedTask?.Id, _shutdown.Token);
            UpdateAgvs(fleetStatus);
            ConnectionStatus = "MES \u5DF2\u8FDE\u63A5";
            var primary = fleetStatus.FirstOrDefault()?.Snapshot;
            AgvStatus = primary is null ? "\u65E0 AGV \u6570\u636E" : primary.Online ? $"\u5728\u7EBF / {primary.ControlOwner}" : "\u79BB\u7EBF";
            AgvStation = primary?.CurrentStationId ?? "-";
            var primaryStatus = fleetStatus.FirstOrDefault();
            AgvExecutionStatus = primaryStatus?.ActiveTask is not { } active
                ? "\u65E0\u6D3B\u52A8\u8FD0\u8F93\u4EFB\u52A1"
                : $"MES {active.MesStatus} / \u8BBE\u5907 {active.DeviceState ?? "\u672A\u77E5"} -> {active.TargetStationId ?? "-"}";
            LastRefreshAt = DateTimeOffset.UtcNow;
            IsDataStale = false;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ConnectionStatus = "MES \u4E0D\u53EF\u7528";
            Message = exception.Message;
            IsDataStale = true;
        }
        finally
        {
            IsRefreshing = false;
            _refreshGate.Release();
        }
    }

    public async Task RefreshAgvAsync()
    {
        if (!await TryEnterRefreshAsync()) return;

        IsRefreshing = true;
        try
        {
            var fleetStatus = await _mes.GetAgvFleetStatusAsync(_shutdown.Token);
            UpdateAgvs(fleetStatus);
            BatchStatus = $"AGV \u72B6\u6001\u5DF2\u5237\u65B0\uFF1A{fleetStatus.Count} \u53F0";
            LastRefreshAt = DateTimeOffset.UtcNow;
            IsDataStale = false;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch
        {
            IsDataStale = true;
            throw;
        }
        finally
        {
            IsRefreshing = false;
            _refreshGate.Release();
        }
    }

    public Task ImportBatchFileAsync(string filePath)
    {
        try
        {
            _modules.BatchImport.Import(filePath);
            BatchStatus = _modules.BatchImport.BatchStatus;
            RefreshBatchCommandState();
        }
        catch (Exception exception)
        {
            BatchStatus = $"Batch import failed: {exception.Message}";
            Message = exception.Message;
        }

        return Task.CompletedTask;
    }

    private async Task SubmitBatchAsync()
    {
        _modules.BatchImport.Sort();
        var submitted = 0;
        foreach (var task in BatchTasks.Where(task => task.Status == "\u5F85\u63D0\u4EA4").ToList())
        {
            if (!TryResolveStationCode(task.SourceStation, out var source) ||
                !TryResolveStationCode(task.TargetStation, out var target))
            {
                task.MarkFailed("Source and target stations must be valid station codes or station IDs.");
                continue;
            }

            try
            {
                await _mes.CreateTaskAsync(
                    source,
                    target,
                    task.Priority,
                    NormalizeOptionalText(task.Description),
                    NormalizeOptionalText(task.TaskId),
                    _shutdown.Token);
                task.MarkSubmitted();
                submitted++;
            }
            catch (Exception exception)
            {
                task.MarkFailed(exception.Message);
            }
        }

        var pending = BatchTasks.Count(task => task.Status == "���ύ");
        BatchStatus = $"Batch submission complete: {submitted} succeeded, {pending} pending";
        RefreshBatchCommandState();
        await RefreshAsync();
    }

    private bool TryResolveStationCode(string value, out int code)
    {
        var normalized = value.Trim();
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
        {
            // When MES has returned a profile catalog, only enabled profile
            // station codes are accepted. If an isolated unit test does not
            // provide a catalog, retain the previous numeric-code behavior and
            // let the MES API perform the final validation.
            var parsedCode = code;
            return _stationCatalog.Count == 0 || _stationCatalog.Any(station => station.Enabled && station.Code == parsedCode);
        }

        var station = _stationCatalog
            .Where(item => item.Enabled)
            .FirstOrDefault(item =>
                string.Equals(item.AgvStationId, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, normalized, StringComparison.OrdinalIgnoreCase));
        code = station?.Code ?? -1;
        return station is not null;
    }

    private void SortBatchTasks()
    {
        var sorted = BatchTasks
            .OrderByDescending(task => task.Priority)
            .ThenBy(task => task.PlannedTime ?? DateTime.MaxValue)
            .ThenBy(task => task.SourceRowNumber)
            .ToList();
        BatchTasks.Clear();
        foreach (var task in sorted) BatchTasks.Add(task);
        RefreshBatchCommandState();
    }

    private void UpdateAgvs(IReadOnlyList<AgvFleetDashboardStatus> statuses)
    {
        var selectedId = SelectedAgv?.AgvId;
        var byId = Agvs.ToDictionary(row => row.AgvId, StringComparer.Ordinal);
        foreach (var status in statuses)
        {
            if (byId.TryGetValue(status.Snapshot.AgvId, out var row)) row.Update(status);
            else Agvs.Add(new AgvRowViewModel(status));
        }
        foreach (var row in Agvs.Where(row => statuses.All(status => status.Snapshot.AgvId != row.AgvId)).ToList()) Agvs.Remove(row);
        SelectedAgv = Agvs.FirstOrDefault(row => row.AgvId == selectedId) ?? Agvs.FirstOrDefault();
        RefreshAgvCommandState();
    }

    private bool CanControlSelectedAgv(string command) => SelectedAgv is { Online: true, CurrentTaskId: not null } agv && agv.Supports(command);

    private async Task ExecuteAgvCommandAsync(string command)
    {
        if (SelectedAgv is not { } agv || agv.CurrentTaskId is not { } taskId) return;
        var result = await _mes.ExecuteAgvCommandAsync(agv.AgvId, command, taskId, _shutdown.Token)
            ?? throw new InvalidOperationException($"AGV {agv.AgvId} returned no result for '{command}'.");
        if (!string.IsNullOrWhiteSpace(result.LastError) ||
            string.Equals(result.State, "failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.State, "error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(result.LastError ?? $"AGV {agv.AgvId} rejected '{command}'.");
        }

        ActionStatus = $"AGV {agv.AgvId} {command} command accepted ({result.State}).";
        BatchStatus = $"Sent {command} command to {agv.AgvId}";
        await RefreshAgvAsync();
        await RefreshAsync();
    }

    private async Task LoadStationsAsync()
    {
        var sourceCode = NewTaskSourceStation?.Code;
        var targetCode = NewTaskTargetStation?.Code;
        var stations = (await _mes.GetStationsAsync(_shutdown.Token)).ToList();
        _stationCatalog = stations;

        AvailableStations.Clear();
        foreach (var station in stations.Where(station => station.Enabled).OrderBy(station => station.Code))
        {
            AvailableStations.Add(station);
        }
        NewTaskSourceStation = sourceCode is { } source
            ? AvailableStations.FirstOrDefault(station => station.Code == source)
            : null;
        NewTaskTargetStation = targetCode is { } target
            ? AvailableStations.FirstOrDefault(station => station.Code == target)
            : null;
        RefreshCreateTaskCommandState();
    }

    private bool CanPlanRoute() =>
        NewTaskSourceStation is { Enabled: true } source &&
        NewTaskTargetStation is { Enabled: true } target &&
        source.Code != target.Code &&
        AvailableStations.Any(station => station.Code == source.Code) &&
        AvailableStations.Any(station => station.Code == target.Code);

    private bool CanCreateTask() =>
        CanPlanRoute() &&
        NewTaskPriority >= 0 &&
        PlannedRoute is { Stations.Count: >= 2 } route &&
        string.Equals(route.Stations[0], NewTaskSourceStation!.AgvStationId, StringComparison.Ordinal) &&
        string.Equals(route.Stations[^1], NewTaskTargetStation!.AgvStationId, StringComparison.Ordinal) &&
        (route.SourceStationId is null || string.Equals(route.SourceStationId, NewTaskSourceStation.AgvStationId, StringComparison.Ordinal)) &&
        (route.TargetStationId is null || string.Equals(route.TargetStationId, NewTaskTargetStation.AgvStationId, StringComparison.Ordinal));
    private bool CanDispatchTask() => SelectedTask?.Status == "Created";
    private bool HasOperator => !string.IsNullOrWhiteSpace(OperatorName);
    private bool CanApplyManualArrival() =>
        IsManualArrivalAvailable && SelectedTask?.Status is "MovingToPickup" or "MovingToDropoff";

    private async Task PlanRouteAsync()
    {
        if (NewTaskSourceStation is not { } source || NewTaskTargetStation is not { } target) return;

        PlannedRoute = null;
        RoutePreview = "\u6B63\u5728\u8BA1\u7B97\u8DEF\u7EBF...";
        try
        {
            var path = await _mes.PlanPathAsync(source.AgvStationId, target.AgvStationId, null, _shutdown.Token);
            if (NewTaskSourceStation?.Code != source.Code || NewTaskTargetStation?.Code != target.Code)
            {
                RoutePreview = "\u7AD9\u70B9\u5DF2\u53D8\u66F4\uFF0C\u8BF7\u91CD\u65B0\u9884\u89C8\u8DEF\u7EBF\u3002";
                return;
            }
            if (path.Stations.Count < 2 ||
                !string.Equals(path.Stations[0], source.AgvStationId, StringComparison.Ordinal) ||
                !string.Equals(path.Stations[^1], target.AgvStationId, StringComparison.Ordinal) ||
                (path.SourceStationId is not null && !string.Equals(path.SourceStationId, source.AgvStationId, StringComparison.Ordinal)) ||
                (path.TargetStationId is not null && !string.Equals(path.TargetStationId, target.AgvStationId, StringComparison.Ordinal)))
            {
                RoutePreview = "MES 返回的路线与选定站点不一致，请重新选择站点后再预览。";
                return;
            }
            PlannedRoute = path;
            RoutePreview = $"{string.Join(" \u2192 ", path.Stations)}\uFF08\u6210\u672C {path.Cost:0.##}\uFF09";
            RefreshCreateTaskCommandState();
        }
        catch (Exception exception)
        {
            RoutePreview = $"\u8DEF\u7EBF\u9884\u89C8\u5931\u8D25\uFF1A{exception.Message}";
            RefreshCreateTaskCommandState();
            throw;
        }
    }

    private async Task CreateTaskAsync()
    {
        if (NewTaskSourceStation is not { } source || NewTaskTargetStation is not { } target) return;

        var created = await _mes.CreateTaskAsync(
            source.Code,
            target.Code,
            NewTaskPriority,
            NormalizeOptionalText(NewTaskDescription),
            NormalizeOptionalText(NewTaskExternalId),
            _shutdown.Token);
        TaskFilterDate = DateTime.UtcNow.Date;
        ActionStatus = $"\u4EFB\u52A1 {created.Id} \u5DF2\u521B\u5EFA\uFF0C\u8BF7\u5728\u4EFB\u52A1\u5217\u8868\u4E2D\u663E\u5F0F\u6D3E\u53D1\u3002";
        await RefreshAsync(created.Id);
    }

    private async Task DispatchTaskAsync()
    {
        if (SelectedTask is null || SelectedTask.Status != "Created") return;

        var dispatched = await _mes.DispatchTaskAsync(SelectedTask.Id, _shutdown.Token);
        ActionStatus = $"\u4EFB\u52A1 {dispatched.Id} \u5DF2\u6D3E\u53D1\uFF0C\u7B49\u5F85 AGV \u6267\u884C\u3002";
        await RefreshAsync(dispatched.Id);
    }

    private async Task ArriveAsync()
    {
        if (!IsManualArrivalAvailable || _simulator is null || SelectedTask is null) return;

        var deviceTaskId = SelectedTask.Status == "MovingToDropoff" ? TransportOperationIds.Dropoff(SelectedTask.Id) : TransportOperationIds.Pickup(SelectedTask.Id);
        await _simulator.ApplyControlAsync(deviceTaskId, "arrive", _shutdown.Token);
        await _mes.MarkArrivedAsync(SelectedTask.Id, _shutdown.Token);
        await RefreshAsync();
    }

    private async Task ConfirmPickupAsync()
    {
        if (SelectedTask is null) return;
        await _mes.ConfirmPickupAsync(SelectedTask.Id, OperatorName.Trim(), _shutdown.Token);
        ActionStatus = $"\u4EFB\u52A1 {SelectedTask.Id} \u5DF2\u786E\u8BA4\u53D6\u8D27\uFF0C\u7EE7\u7EED\u524D\u5F80\u653E\u8D27\u7AD9\u3002";
        await RefreshAsync();
    }
    private async Task ConfirmDropoffAsync()
    {
        if (SelectedTask is null) return;
        await _mes.ConfirmDropoffAsync(SelectedTask.Id, OperatorName.Trim(), _shutdown.Token);
        ActionStatus = $"\u4EFB\u52A1 {SelectedTask.Id} \u5DF2\u786E\u8BA4\u653E\u8D27\uFF0C\u6D41\u7A0B\u5DF2\u5B8C\u6210\u3002";
        await RefreshAsync();
    }
    private async Task RetryAsync()
    {
        if (SelectedTask is null) return;
        await _mes.RetryAsync(SelectedTask.Id, _shutdown.Token);
        ActionStatus = $"\u4EFB\u52A1 {SelectedTask.Id} \u5DF2\u91CD\u8BD5\uFF0C\u7B49\u5F85 AGV \u6267\u884C\u3002";
        await RefreshAsync();
    }
    private async Task RecoverAsync()
    {
        if (SelectedTask is null) return;
        await _mes.RecoverAsync(SelectedTask.Id, _shutdown.Token);
        ActionStatus = $"\u4EFB\u52A1 {SelectedTask.Id} \u5DF2\u91CD\u65B0\u8BFB\u53D6\u72B6\u6001\u3002";
        await RefreshAsync();
    }
    private async Task CancelAsync()
    {
        if (SelectedTask is null) return;
        await _mes.CancelAsync(SelectedTask.Id, OperatorName.Trim(), _shutdown.Token);
        ActionStatus = $"\u4EFB\u52A1 {SelectedTask.Id} \u5DF2\u53D6\u6D88\u3002";
        await RefreshAsync();
    }
    private async Task ApplySimulatorControlAsync(string mode) { if (!IsSimulatorPanelVisible || _simulator is null) return; await _simulator.ApplyControlAsync(mode, _shutdown.Token); await RefreshAsync(); }

    private AsyncCommand CreateActionCommand(string actionName, Func<Task> action, Func<bool>? canExecute = null) =>
        new(
            () => ExecuteActionAsync(actionName, action),
            () => !IsActionInProgress && (canExecute?.Invoke() ?? true));

    private async Task ExecuteActionAsync(string actionName, Func<Task> action)
    {
        bool entered;
        try { entered = _actionGate.Wait(0); }
        catch (ObjectDisposedException) { return; }
        if (!entered)
        {
            Message = "已有操作正在执行，请稍候。";
            return;
        }

        CurrentAction = actionName;
        IsActionInProgress = true;
        Message = string.Empty;
        var runningStatus = $"正在执行：{actionName}";
        ActionStatus = runningStatus;
        try { await action(); }
        catch (Exception exception)
        {
            ActionStatus = "\u64CD\u4F5C\u5931\u8D25";
            Message = exception.Message;
        }
        finally
        {
            if (ActionStatus == runningStatus) ActionStatus = $"{actionName}完成";
            CurrentAction = string.Empty;
            IsActionInProgress = false;
            try { _actionGate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        try { while (await _timer.WaitForNextTickAsync(cancellationToken)) await RefreshAsync(); }
        catch (OperationCanceledException) { }
    }

    private void RefreshCommandState()
    {
        foreach (var command in new[] { DispatchTaskCommand, ArriveCommand, ConfirmPickupCommand, ConfirmDropoffCommand, RetryCommand, RecoverCommand, CancelCommand }.OfType<AsyncCommand>()) command.RaiseCanExecuteChanged();
    }

    private void RefreshAllCommandState()
    {
        RefreshCommandState();
        RefreshCreateTaskCommandState();
        RefreshAgvCommandState();
        RefreshBatchCommandState();
        foreach (var command in new[]
        {
            SimulatorArriveCommand,
            SimulatorFailCommand,
            SimulatorTimeoutCommand,
            SimulatorOfflineCommand,
            SimulatorRecoverCommand,
            RefreshAgvCommand,
            QueryTasksCommand,
            RefreshTasksCommand
        }.OfType<AsyncCommand>()) command.RaiseCanExecuteChanged();
    }

    private void InvalidateRoutePreview()
    {
        PlannedRoute = null;
        RoutePreview = "\u8BF7\u9009\u62E9\u8D77\u70B9\u548C\u7EC8\u70B9\u540E\u9884\u89C8\u8DEF\u7EBF\u3002";
        RefreshCreateTaskCommandState();
    }

    private void RefreshCreateTaskCommandState()
    {
        foreach (var command in new[] { PlanRouteCommand, CreateTaskCommand }.OfType<AsyncCommand>()) command.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(TaskFormStatus));
    }

    private string GetTaskFormStatus()
    {
        if (NewTaskSourceStation is null || NewTaskTargetStation is null) return "\u8BF7\u9009\u62E9\u8D77\u70B9\u548C\u7EC8\u70B9\u3002";
        if (NewTaskSourceStation.Code == NewTaskTargetStation.Code) return "\u8D77\u70B9\u548C\u7EC8\u70B9\u4E0D\u80FD\u76F8\u540C\u3002";
        if (NewTaskPriority < 0) return "\u4F18\u5148\u7EA7\u4E0D\u80FD\u4E3A\u8D1F\u6570\u3002";
        return PlannedRoute is null ? "\u8BF7\u5148\u9884\u89C8\u5E76\u786E\u8BA4\u8DEF\u7EBF\u3002" : "\u8DEF\u7EBF\u5DF2\u786E\u8BA4\uFF0C\u53EF\u521B\u5EFA\u4EFB\u52A1\u3002";
    }

    private static string? NormalizeOptionalText(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void RefreshAgvCommandState()
    {
        foreach (var command in new[] { PauseAgvCommand, ResumeAgvCommand, CancelAgvCommand }.OfType<AsyncCommand>()) command.RaiseCanExecuteChanged();
    }

    private void RefreshBatchCommandState()
    {
        foreach (var command in new[] { SortBatchCommand, SubmitBatchCommand }.OfType<AsyncCommand>()) command.RaiseCanExecuteChanged();
    }

    private void RequestTaskDetailRefresh()
    {
        CancelPendingDetailRefresh();
        _detailRefresh = new CancellationTokenSource();
        _ = RefreshSelectedTaskDetailAsync(SelectedTask?.Id, _detailRefresh.Token);
    }

    private async Task RefreshSelectedTaskDetailAsync(Guid? taskId, CancellationToken cancellationToken)
    {
        try { await LoadTaskDetailAsync(taskId, cancellationToken); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Message = exception.Message; }
    }

    private async Task LoadTaskDetailAsync(Guid? taskId, CancellationToken cancellationToken)
    {
        TaskEvents.Clear();
        if (taskId is not { } id) return;
        var detail = await _mes.GetTaskDetailAsync(id, cancellationToken);
        if (detail is null || id != SelectedTask?.Id) return;
        foreach (var taskEvent in detail.Events) TaskEvents.Add(TaskEventRowViewModel.From(taskEvent));
    }

    private void CancelPendingDetailRefresh() { _detailRefresh?.Cancel(); _detailRefresh?.Dispose(); _detailRefresh = null; }

    private async Task<bool> TryEnterRefreshAsync()
    {
        try
        {
            await _refreshGate.WaitAsync(_shutdown.Token);
            return true;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        CancelPendingDetailRefresh();
        _timer.Dispose();
        _refreshGate.Dispose();
        _actionGate.Dispose();
        _shutdown.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
