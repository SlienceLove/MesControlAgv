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
    private readonly ControlCenterCommandCoordinator _commands;
    private readonly ControlCenterViewModel _modules;
    private PeriodicTimer? _timer;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private CancellationTokenSource? _detailRefresh;
    private Task? _refreshLoop;
    private bool _suppressDetailRefresh;
    private string _message = string.Empty;
    private string _actionStatus = "\u8BF7\u521B\u5EFA\u4EFB\u52A1\uFF0C\u7136\u540E\u4ECE\u4EFB\u52A1\u5217\u8868\u4E2D\u663E\u5F0F\u6D3E\u53D1\u3002";
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
    private TimeSpan _taskRefreshInterval = DashboardRuntimeSettings.Default.TaskRefreshInterval;

    public MainViewModel(
        IMesClient mes,
        ISimulatorControlClient? simulator = null,
        ControlCenterModuleRegistry? moduleRegistry = null,
        IMapLayoutSource? mapLayoutSource = null)
    {
        _mes = mes;
        _simulator = simulator;
        _commands = new ControlCenterCommandCoordinator(mes, simulator);
        ModuleRegistry = moduleRegistry ?? ControlCenterModuleRegistry.CreateStandard();
        WorkflowEditor = new WorkflowEditorViewModel(new WorkflowStore(), _mes, () => OperatorName);
        Readiness = new ReadinessViewModel(_mes, mapLayoutSource);
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
        SortBatchCommand = CreateActionCommand("\u6392\u5E8F\u6279\u91CF\u4EFB\u52A1", () => { _modules.BatchImport.Sort(); RefreshBatchCommandState(); return Task.CompletedTask; }, () => BatchTasks.Count > 1);
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
    public ReadinessViewModel Readiness { get; }
    public KpiDashboardViewModel Kpi { get; }
    public ControlCenterModuleRegistry ModuleRegistry { get; }
    public ControlCenterViewModel Modules => _modules;
    public TaskMonitorViewModel TaskMonitor => _modules.TaskMonitor;
    public AgvCommunicationViewModel AgvCommunication => _modules.AgvCommunication;
    public BatchImportViewModel BatchImport => _modules.BatchImport;

    public TaskRowViewModel? SelectedTask
    {
        get => _modules.TaskMonitor.SelectedTask;
        set
        {
            if (ReferenceEquals(_modules.TaskMonitor.SelectedTask, value)) return;
            _modules.TaskMonitor.SelectedTask = value;
            OnPropertyChanged(nameof(SelectedTask));
            RefreshCommandState();
            if (!_suppressDetailRefresh) RequestTaskDetailRefresh();
        }
    }

    public AgvRowViewModel? SelectedAgv
    {
        get => _modules.AgvCommunication.SelectedAgv;
        set
        {
            if (ReferenceEquals(_modules.AgvCommunication.SelectedAgv, value)) return;
            _modules.AgvCommunication.SelectedAgv = value;
            OnPropertyChanged(nameof(SelectedAgv));
            RefreshAgvCommandState();
        }
    }

    public string ConnectionStatus
    {
        get => _modules.TaskMonitor.ConnectionStatus;
        private set
        {
            if (string.Equals(_modules.TaskMonitor.ConnectionStatus, value, StringComparison.Ordinal)) return;
            _modules.TaskMonitor.ConnectionStatus = value;
            OnPropertyChanged(nameof(ConnectionStatus));
        }
    }
    public string AgvStatus
    {
        get => _modules.AgvCommunication.AgvStatus;
        private set
        {
            if (string.Equals(_modules.AgvCommunication.AgvStatus, value, StringComparison.Ordinal)) return;
            _modules.AgvCommunication.AgvStatus = value;
            OnPropertyChanged(nameof(AgvStatus));
        }
    }
    public string AgvStation
    {
        get => _modules.AgvCommunication.AgvStation;
        private set
        {
            if (string.Equals(_modules.AgvCommunication.AgvStation, value, StringComparison.Ordinal)) return;
            _modules.AgvCommunication.AgvStation = value;
            OnPropertyChanged(nameof(AgvStation));
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
        get => _modules.AgvCommunication.AgvExecutionStatus;
        private set
        {
            if (string.Equals(_modules.AgvCommunication.AgvExecutionStatus, value, StringComparison.Ordinal)) return;
            _modules.AgvCommunication.AgvExecutionStatus = value;
            OnPropertyChanged(nameof(AgvExecutionStatus));
        }
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
        get => _modules.BatchImport.BatchStatus;
        private set
        {
            if (string.Equals(_modules.BatchImport.BatchStatus, value, StringComparison.Ordinal)) return;
            _modules.BatchImport.BatchStatus = value;
            OnPropertyChanged(nameof(BatchStatus));
        }
    }

    public DateTime? TaskFilterDate
    {
        get => _modules.TaskMonitor.TaskFilterDate;
        set
        {
            if (value is not { } date) return;
            if (DateOnly.FromDateTime(_modules.TaskMonitor.TaskFilterDate ?? DateTime.MinValue) == DateOnly.FromDateTime(date.Date)) return;
            _modules.TaskMonitor.TaskFilterDate = date.Date;
            OnPropertyChanged(nameof(TaskFilterDate));
        }
    }

    private DateOnly CurrentTaskDate => DateOnly.FromDateTime(TaskFilterDate ?? DateTime.UtcNow.Date);

    public bool IsSimulatorMode => _simulator is not null;
    public bool IsPhysicalMode => !IsSimulatorMode;
    public TimeSpan TaskRefreshInterval
    {
        get => _taskRefreshInterval;
        private set => SetField(ref _taskRefreshInterval, value);
    }
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
        await Readiness.RefreshAsync(_shutdown.Token);
        if (_refreshLoop is not null)
        {
            return;
        }

        TaskRefreshInterval = await ResolveTaskRefreshIntervalAsync(_shutdown.Token);
        _timer = new PeriodicTimer(TaskRefreshInterval);
        _refreshLoop = RefreshLoopAsync(_timer, _shutdown.Token);
    }

    public async Task RefreshAsync(Guid? preferredTaskId = null)
    {
        if (!await TryEnterRefreshAsync()) return;

        IsRefreshing = true;
        try
        {
            // 随任务和车队快照刷新配置站点目录；配置变更必须使路线预览失效，
            // 避免使用过期站点元数据创建任务。
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
            Readiness.UpdateFleet(fleetStatus);
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
            Readiness.UpdateFleet(fleetStatus);
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
            BatchStatus = $"批量导入失败：{exception.Message}";
            Message = exception.Message;
        }

        return Task.CompletedTask;
    }

    private async Task SubmitBatchAsync()
    {
        _modules.BatchImport.Sort();
        var submitted = 0;
        var pendingCount = 0;
        foreach (var task in BatchTasks.Where(task => task.Status == "\u5F85\u63D0\u4EA4").ToList())
        {
            if (!TryResolveStationCode(task.SourceStation, out var source) ||
                !TryResolveStationCode(task.TargetStation, out var target))
            {
                task.MarkFailed("起点和终点必须是有效的站点编码或站点 ID。");
                continue;
            }

            try
            {
                await _commands.CreateTaskAsync(
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

        pendingCount = BatchTasks.Count(task => task.Status == "\u5F85\u63D0\u4EA4");
        BatchStatus = $"批量提交完成：成功 {submitted} 条，待提交 {pendingCount} 条";
        RefreshBatchCommandState();
        await RefreshAsync();
    }

    private bool TryResolveStationCode(string value, out int code)
    {
        var normalized = value.Trim();
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
        {
            // MES 返回配置目录后只接受启用站点编码；隔离单测未提供目录时，
            // 保留原有数字编码行为，最终由 MES 接口校验。
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

    private void UpdateAgvs(IReadOnlyList<AgvFleetDashboardStatus> statuses)
    {
        _modules.AgvCommunication.UpdateFleet(statuses);
        SelectedAgv = _modules.AgvCommunication.SelectedAgv;
        RefreshAgvCommandState();
    }

    private bool CanControlSelectedAgv(string command) => SelectedAgv is { Online: true, CurrentTaskId: not null } agv && agv.Supports(command);

    private async Task ExecuteAgvCommandAsync(string command)
    {
        if (SelectedAgv is not { } agv || agv.CurrentTaskId is not { } taskId) return;
        var result = await _commands.ExecuteAgvCommandAsync(agv.AgvId, command, taskId, _shutdown.Token);

        ActionStatus = $"AGV {agv.AgvId} 已接受“{command}”操作（{result.State}）。";
        BatchStatus = $"已向 {agv.AgvId} 发送“{command}”操作";
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
        WorkflowEditor.ApplyProfileStations(stations);
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

        var created = await _commands.CreateTaskAsync(
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

        var dispatched = await _commands.DispatchTaskAsync(SelectedTask.Id, _shutdown.Token);
        ActionStatus = $"\u4EFB\u52A1 {dispatched.Id} \u5DF2\u6D3E\u53D1\uFF0C\u7B49\u5F85 AGV \u6267\u884C\u3002";
        await RefreshAsync(dispatched.Id);
    }

    private async Task ArriveAsync()
    {
        if (!IsManualArrivalAvailable || _simulator is null || SelectedTask is null) return;

        await _commands.MarkSimulatorArrivalAsync(
            SelectedTask.Id,
            SelectedTask.Status == "MovingToDropoff",
            _shutdown.Token);
        await RefreshAsync();
    }

    private async Task ConfirmPickupAsync()
    {
        if (SelectedTask is null) return;
        await _commands.ConfirmPickupAsync(SelectedTask.Id, OperatorName.Trim(), _shutdown.Token);
        ActionStatus = $"\u4EFB\u52A1 {SelectedTask.Id} \u5DF2\u786E\u8BA4\u53D6\u8D27\uFF0C\u7EE7\u7EED\u524D\u5F80\u653E\u8D27\u7AD9\u3002";
        await RefreshAsync();
    }
    private async Task ConfirmDropoffAsync()
    {
        if (SelectedTask is null) return;
        await _commands.ConfirmDropoffAsync(SelectedTask.Id, OperatorName.Trim(), _shutdown.Token);
        ActionStatus = $"\u4EFB\u52A1 {SelectedTask.Id} \u5DF2\u786E\u8BA4\u653E\u8D27\uFF0C\u6D41\u7A0B\u5DF2\u5B8C\u6210\u3002";
        await RefreshAsync();
    }
    private async Task RetryAsync()
    {
        if (SelectedTask is null) return;
        await _commands.RetryAsync(SelectedTask.Id, _shutdown.Token);
        ActionStatus = $"\u4EFB\u52A1 {SelectedTask.Id} \u5DF2\u91CD\u8BD5\uFF0C\u7B49\u5F85 AGV \u6267\u884C\u3002";
        await RefreshAsync();
    }
    private async Task RecoverAsync()
    {
        if (SelectedTask is null) return;
        await _commands.RecoverAsync(SelectedTask.Id, _shutdown.Token);
        ActionStatus = $"\u4EFB\u52A1 {SelectedTask.Id} \u5DF2\u91CD\u65B0\u8BFB\u53D6\u72B6\u6001\u3002";
        await RefreshAsync();
    }
    private async Task CancelAsync()
    {
        if (SelectedTask is null) return;
        await _commands.CancelAsync(SelectedTask.Id, OperatorName.Trim(), _shutdown.Token);
        ActionStatus = $"\u4EFB\u52A1 {SelectedTask.Id} \u5DF2\u53D6\u6D88\u3002";
        await RefreshAsync();
    }
    private async Task ApplySimulatorControlAsync(string mode) { if (!IsSimulatorPanelVisible || _simulator is null) return; await _commands.ApplySimulatorControlAsync(mode, _shutdown.Token); await RefreshAsync(); }

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
            ActionStatus = "操作失败";
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

    private async Task<TimeSpan> ResolveTaskRefreshIntervalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _mes.GetRuntimeSettingsAsync(cancellationToken);
            return settings.TaskRefreshInterval > TimeSpan.Zero
                ? settings.TaskRefreshInterval
                : DashboardRuntimeSettings.Default.TaskRefreshInterval;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DashboardRuntimeSettings.Default.TaskRefreshInterval;
        }
    }

    private async Task RefreshLoopAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try { while (await timer.WaitForNextTickAsync(cancellationToken)) await RefreshAsync(); }
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
        _timer?.Dispose();
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
