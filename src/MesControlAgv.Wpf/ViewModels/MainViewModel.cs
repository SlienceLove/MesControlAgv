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
    private CancellationTokenSource? _detailRefresh;
    private Task? _refreshLoop;
    private TaskRowViewModel? _selectedTask;
    private AgvRowViewModel? _selectedAgv;
    private bool _suppressDetailRefresh;
    private string _connectionStatus = "\u6B63\u5728\u8FDE\u63A5 MES";
    private string _agvStatus = "\u672A\u77E5";
    private string _agvStation = "-";
    private string _message = string.Empty;
    private string _batchStatus = "\u8BF7\u9009\u62E9 CSV \u6216 XLSX \u6587\u4EF6\u5BFC\u5165\u4EFB\u52A1";
    private DateTime? _taskFilterDate = DateTime.UtcNow.Date;

    public MainViewModel(IMesClient mes, ISimulatorControlClient? simulator = null, ControlCenterModuleRegistry? moduleRegistry = null)
    {
        _mes = mes;
        _simulator = simulator;
        ModuleRegistry = moduleRegistry ?? ControlCenterModuleRegistry.CreateStandard();
        WorkflowEditor = new WorkflowEditorViewModel(new WorkflowStore());
        _modules = new ControlCenterViewModel(WorkflowEditor, ModuleRegistry);
        Kpi = _modules.KpiDashboard;
        CreateTaskCommand = new AsyncCommand(() => ExecuteActionAsync(CreateTaskAsync));
        ArriveCommand = new AsyncCommand(() => ExecuteActionAsync(ArriveAsync), () => SelectedTask?.Status is "MovingToPickup" or "MovingToDropoff");
        ConfirmPickupCommand = new AsyncCommand(() => ExecuteActionAsync(ConfirmPickupAsync), () => SelectedTask?.Status == "WaitingPickupConfirmation");
        ConfirmDropoffCommand = new AsyncCommand(() => ExecuteActionAsync(ConfirmDropoffAsync), () => SelectedTask?.Status == "WaitingDropoffConfirmation");
        RetryCommand = new AsyncCommand(() => ExecuteActionAsync(RetryAsync), () => SelectedTask?.Status == "Failed");
        RecoverCommand = new AsyncCommand(() => ExecuteActionAsync(RecoverAsync), () => SelectedTask?.Status == "Unknown");
        CancelCommand = new AsyncCommand(() => ExecuteActionAsync(CancelAsync), () => SelectedTask is { Status: not "Completed" and not "Cancelled" });
        SimulatorArriveCommand = new AsyncCommand(() => ExecuteActionAsync(() => ApplySimulatorControlAsync("arrive")), () => _simulator is not null);
        SimulatorFailCommand = new AsyncCommand(() => ExecuteActionAsync(() => ApplySimulatorControlAsync("fail")), () => _simulator is not null);
        SimulatorTimeoutCommand = new AsyncCommand(() => ExecuteActionAsync(() => ApplySimulatorControlAsync("timeout")), () => _simulator is not null);
        SimulatorOfflineCommand = new AsyncCommand(() => ExecuteActionAsync(() => ApplySimulatorControlAsync("offline")), () => _simulator is not null);
        SimulatorRecoverCommand = new AsyncCommand(() => ExecuteActionAsync(() => ApplySimulatorControlAsync("recover")), () => _simulator is not null);
        RefreshAgvCommand = new AsyncCommand(() => ExecuteActionAsync(RefreshAgvAsync));
        QueryTasksCommand = new AsyncCommand(() => ExecuteActionAsync(() => RefreshAsync()));
        RefreshTasksCommand = new AsyncCommand(() => ExecuteActionAsync(() => RefreshAsync()));
        PauseAgvCommand = new AsyncCommand(() => ExecuteActionAsync(() => ExecuteAgvCommandAsync("pause")), () => CanControlSelectedAgv("pause"));
        ResumeAgvCommand = new AsyncCommand(() => ExecuteActionAsync(() => ExecuteAgvCommandAsync("resume")), () => CanControlSelectedAgv("resume"));
        CancelAgvCommand = new AsyncCommand(() => ExecuteActionAsync(() => ExecuteAgvCommandAsync("cancel")), () => CanControlSelectedAgv("cancel"));
        SortBatchCommand = new AsyncCommand(() => { SortBatchTasks(); return Task.CompletedTask; }, () => BatchTasks.Count > 1);
        SubmitBatchCommand = new AsyncCommand(() => ExecuteActionAsync(SubmitBatchAsync), () => BatchTasks.Any(task => task.Status == "\u5F85\u63D0\u4EA4"));
        ClearBatchCommand = new AsyncCommand(() =>
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

#if DEBUG
    public bool IsSimulatorPanelVisible => _simulator is not null;
#else
    public bool IsSimulatorPanelVisible => false;
#endif

    public ICommand CreateTaskCommand { get; }
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
        try
        {
            var tasks = await _mes.GetTasksAsync(CurrentTaskDate, _shutdown.Token);
            var fleet = await _mes.GetAgvFleetAsync(_shutdown.Token);
            await Kpi.RefreshAsync(_mes, CurrentTaskDate, _shutdown.Token);
            var selectedId = preferredTaskId ?? SelectedTask?.Id;
            Tasks.Clear();
            foreach (var task in tasks) Tasks.Add(TaskRowViewModel.From(task));
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
            UpdateAgvs(fleet);
            ConnectionStatus = "MES \u5DF2\u8FDE\u63A5";
            var primary = fleet.FirstOrDefault();
            AgvStatus = primary is null ? "\u65E0 AGV \u6570\u636E" : primary.Online ? $"\u5728\u7EBF / {primary.ControlOwner}" : "\u79BB\u7EBF";
            AgvStation = primary?.CurrentStationId ?? "-";
            Message = string.Empty;
        }
        catch (Exception exception)
        {
            ConnectionStatus = "MES \u4E0D\u53EF\u7528";
            Message = exception.Message;
        }
    }

    public async Task RefreshAgvAsync()
    {
        var fleet = await _mes.GetAgvFleetAsync(_shutdown.Token);
        UpdateAgvs(fleet);
        BatchStatus = $"AGV \u72B6\u6001\u5DF2\u5237\u65B0\uFF1A{fleet.Count} \u53F0";
    }

    #if false
    public Task ImportBatchFileAsync(string filePath)
    {
        try
        {
            var result = _batchParser.Parse(filePath);
            BatchTasks.Clear();
            BatchImportIssues.Clear();
            foreach (var issue in result.Issues) BatchImportIssues.Add($"�?{issue.SourceRowNumber} 琛岋細{issue.Message}");
            foreach (var task in result.Tasks) BatchTasks.Add(new BatchTaskRowViewModel(task));
            BatchStatus = $"宸插鍏?{BatchTasks.Count} 鏉′换鍔★紝闂�?{BatchImportIssues.Count} 鏉★紱鍙紪杈戜紭鍏堢骇鍚庢彁浜?;
            RefreshBatchCommandState();
        }
        catch (Exception exception)
        {
            BatchStatus = $"瀵煎叆澶辫触锛歿exception.Message}";
            Message = exception.Message;
        }
        return Task.CompletedTask;
    }

    private async Task SubmitBatchAsync()
    {
        SortBatchTasks();
        var submitted = 0;
        foreach (var task in BatchTasks.Where(task => task.Status == "寰呮彁浜?).ToList())
        {
            if (!TryResolveStationCode(task.SourceStation, out var source) ||
                !TryResolveStationCode(task.TargetStation, out var target))
            {
                task.MarkFailed("璧风�?缁堢偣蹇呴』鏄暟瀛楃紪鐮併€丄GV 绔欑�?ID 鎴栫珯鐐瑰悕�?);
                continue;
            }

            try
            {
                await _mes.CreateTaskAsync(source, target, task.Priority, task.Description, task.TaskId, _shutdown.Token);
                task.MarkSubmitted();
                submitted++;
            }
            catch (Exception exception)
            {
                task.MarkFailed(exception.Message);
            }
        }
        BatchStatus = $"鎵归噺鎻愪氦瀹屾垚锛氭垚�?{submitted} 鏉★紝寰呮彁�?{BatchTasks.Count(task => task.Status == "寰呮彁浜?)} �?;
        RefreshBatchCommandState();
        await RefreshAsync();
    }

    #endif

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
                await _mes.CreateTaskAsync(source, target, task.Priority, task.Description, task.TaskId, _shutdown.Token);
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

    private static bool TryResolveStationCode(string value, out int code)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out code)) return true;

        var station = Stations.All.FirstOrDefault(item =>
            string.Equals(item.AgvStationId, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase));
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

    private void UpdateAgvs(IReadOnlyList<AgvDashboardSnapshot> snapshots)
    {
        var selectedId = SelectedAgv?.AgvId;
        var byId = Agvs.ToDictionary(row => row.AgvId, StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            if (byId.TryGetValue(snapshot.AgvId, out var row)) row.Update(snapshot);
            else Agvs.Add(new AgvRowViewModel(snapshot));
        }
        foreach (var row in Agvs.Where(row => snapshots.All(snapshot => snapshot.AgvId != row.AgvId)).ToList()) Agvs.Remove(row);
        SelectedAgv = Agvs.FirstOrDefault(row => row.AgvId == selectedId) ?? Agvs.FirstOrDefault();
        RefreshAgvCommandState();
    }

    private bool CanControlSelectedAgv(string command) => SelectedAgv is { Online: true, CurrentTaskId: not null } agv && agv.Supports(command);

    private async Task ExecuteAgvCommandAsync(string command)
    {
        if (SelectedAgv is not { } agv || agv.CurrentTaskId is not { } taskId) return;
        await _mes.ExecuteAgvCommandAsync(agv.AgvId, command, taskId, _shutdown.Token);
        BatchStatus = $"Sent {command} command to {agv.AgvId}";
        await RefreshAgvAsync();
        await RefreshAsync();
    }

    private async Task CreateTaskAsync()
    {
        var created = await _mes.CreateTaskAsync(_shutdown.Token);
        TaskFilterDate = DateTime.UtcNow.Date;
        await RefreshAsync(created.Id);
    }

    private async Task ArriveAsync()
    {
        if (SelectedTask is not null)
        {
            if (_simulator is not null)
            {
                var deviceTaskId = SelectedTask.Status == "MovingToDropoff" ? TransportOperationIds.Dropoff(SelectedTask.Id) : TransportOperationIds.Pickup(SelectedTask.Id);
                await _simulator.ApplyControlAsync(deviceTaskId, "arrive", _shutdown.Token);
            }
            await _mes.MarkArrivedAsync(SelectedTask.Id, _shutdown.Token);
        }
        await RefreshAsync();
    }

    private async Task ConfirmPickupAsync() { if (SelectedTask is not null) await _mes.ConfirmPickupAsync(SelectedTask.Id, "wpf-operator", _shutdown.Token); await RefreshAsync(); }
    private async Task ConfirmDropoffAsync() { if (SelectedTask is not null) await _mes.ConfirmDropoffAsync(SelectedTask.Id, "wpf-operator", _shutdown.Token); await RefreshAsync(); }
    private async Task RetryAsync() { if (SelectedTask is not null) await _mes.RetryAsync(SelectedTask.Id, _shutdown.Token); await RefreshAsync(); }
    private async Task RecoverAsync() { if (SelectedTask is not null) await _mes.RecoverAsync(SelectedTask.Id, _shutdown.Token); await RefreshAsync(); }
    private async Task CancelAsync() { if (SelectedTask is not null) await _mes.CancelAsync(SelectedTask.Id, "wpf-operator", _shutdown.Token); await RefreshAsync(); }
    private async Task ApplySimulatorControlAsync(string mode) { if (_simulator is null) return; await _simulator.ApplyControlAsync(mode, _shutdown.Token); await RefreshAsync(); }

    private async Task ExecuteActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { Message = exception.Message; }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        try { while (await _timer.WaitForNextTickAsync(cancellationToken)) await RefreshAsync(); }
        catch (OperationCanceledException) { }
    }

    private void RefreshCommandState()
    {
        foreach (var command in new[] { ArriveCommand, ConfirmPickupCommand, ConfirmDropoffCommand, RetryCommand, RecoverCommand, CancelCommand }.OfType<AsyncCommand>()) command.RaiseCanExecuteChanged();
    }

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
        try { await LoadTaskDetailAsync(taskId, cancellationToken); if (taskId == SelectedTask?.Id) Message = string.Empty; }
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

    public void Dispose()
    {
        _shutdown.Cancel();
        CancelPendingDetailRefresh();
        _timer.Dispose();
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
}
