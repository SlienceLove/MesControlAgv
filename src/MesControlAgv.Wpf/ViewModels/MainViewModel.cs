using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Domain;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IMesClient _mes;
    private readonly ISimulatorControlClient? _simulator;
    private readonly BatchTaskImportParser _batchParser = new();
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(2));
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _detailRefresh;
    private Task? _refreshLoop;
    private TaskRowViewModel? _selectedTask;
    private AgvRowViewModel? _selectedAgv;
    private bool _suppressDetailRefresh;
    private string _connectionStatus = "正在连接 MES";
    private string _agvStatus = "未知";
    private string _agvStation = "-";
    private string _message = string.Empty;
    private string _batchStatus = "请选择 CSV 或 XLSX 文件导入任务";
    private DateTime? _taskFilterDate = DateTime.UtcNow.Date;

    public MainViewModel(IMesClient mes, ISimulatorControlClient? simulator = null)
    {
        _mes = mes;
        _simulator = simulator;
        WorkflowEditor = new WorkflowEditorViewModel(new WorkflowStore());
        Kpi = new KpiDashboardViewModel();
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
        PauseAgvCommand = new AsyncCommand(() => ExecuteActionAsync(() => ExecuteAgvCommandAsync("pause")), CanControlSelectedAgv);
        ResumeAgvCommand = new AsyncCommand(() => ExecuteActionAsync(() => ExecuteAgvCommandAsync("resume")), CanControlSelectedAgv);
        CancelAgvCommand = new AsyncCommand(() => ExecuteActionAsync(() => ExecuteAgvCommandAsync("cancel")), CanControlSelectedAgv);
        SortBatchCommand = new AsyncCommand(() => { SortBatchTasks(); return Task.CompletedTask; }, () => BatchTasks.Count > 1);
        SubmitBatchCommand = new AsyncCommand(() => ExecuteActionAsync(SubmitBatchAsync), () => BatchTasks.Any(task => task.Status == "待提交"));
        ClearBatchCommand = new AsyncCommand(() => { BatchTasks.Clear(); BatchImportIssues.Clear(); BatchStatus = "已清空导入列表"; RefreshBatchCommandState(); return Task.CompletedTask; });
    }

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];
    public ObservableCollection<TaskEventRowViewModel> TaskEvents { get; } = [];
    public ObservableCollection<AgvRowViewModel> Agvs { get; } = [];
    public ObservableCollection<BatchTaskRowViewModel> BatchTasks { get; } = [];
    public ObservableCollection<string> BatchImportIssues { get; } = [];
    public WorkflowEditorViewModel WorkflowEditor { get; }
    public KpiDashboardViewModel Kpi { get; }

    public TaskRowViewModel? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (!SetField(ref _selectedTask, value)) return;
            RefreshCommandState();
            if (!_suppressDetailRefresh) RequestTaskDetailRefresh();
        }
    }

    public AgvRowViewModel? SelectedAgv
    {
        get => _selectedAgv;
        set { if (SetField(ref _selectedAgv, value)) RefreshAgvCommandState(); }
    }

    public string ConnectionStatus { get => _connectionStatus; private set => SetField(ref _connectionStatus, value); }
    public string AgvStatus { get => _agvStatus; private set => SetField(ref _agvStatus, value); }
    public string AgvStation { get => _agvStation; private set => SetField(ref _agvStation, value); }
    public string Message { get => _message; private set => SetField(ref _message, value); }
    public string BatchStatus { get => _batchStatus; private set => SetField(ref _batchStatus, value); }

    public DateTime? TaskFilterDate
    {
        get => _taskFilterDate;
        set
        {
            if (value is not { } date) return;
            SetField(ref _taskFilterDate, date.Date);
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
            try { SelectedTask = Tasks.SingleOrDefault(task => task.Id == selectedId) ?? Tasks.FirstOrDefault(); }
            finally { _suppressDetailRefresh = false; }
            await LoadTaskDetailAsync(SelectedTask?.Id, _shutdown.Token);
            UpdateAgvs(fleet);
            ConnectionStatus = "MES 已连接";
            var primary = fleet.FirstOrDefault();
            AgvStatus = primary is null ? "无 AGV 数据" : primary.Online ? $"在线 / {primary.ControlOwner}" : "离线";
            AgvStation = primary?.CurrentStationId ?? "-";
            Message = string.Empty;
        }
        catch (Exception exception)
        {
            ConnectionStatus = "MES 不可用";
            Message = exception.Message;
        }
    }

    public async Task RefreshAgvAsync()
    {
        var fleet = await _mes.GetAgvFleetAsync(_shutdown.Token);
        UpdateAgvs(fleet);
        BatchStatus = $"AGV 状态已刷新：{fleet.Count} 台";
    }

    public Task ImportBatchFileAsync(string filePath)
    {
        try
        {
            var result = _batchParser.Parse(filePath);
            BatchTasks.Clear();
            BatchImportIssues.Clear();
            foreach (var issue in result.Issues) BatchImportIssues.Add($"第 {issue.SourceRowNumber} 行：{issue.Message}");
            foreach (var task in result.Tasks) BatchTasks.Add(new BatchTaskRowViewModel(task));
            BatchStatus = $"已导入 {BatchTasks.Count} 条任务，问题 {BatchImportIssues.Count} 条；可编辑优先级后提交";
            RefreshBatchCommandState();
        }
        catch (Exception exception)
        {
            BatchStatus = $"导入失败：{exception.Message}";
            Message = exception.Message;
        }
        return Task.CompletedTask;
    }

    private async Task SubmitBatchAsync()
    {
        SortBatchTasks();
        var submitted = 0;
        foreach (var task in BatchTasks.Where(task => task.Status == "待提交").ToList())
        {
            if (!TryResolveStationCode(task.SourceStation, out var source) ||
                !TryResolveStationCode(task.TargetStation, out var target))
            {
                task.MarkFailed("起点/终点必须是数字编码、AGV 站点 ID 或站点名称");
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
        BatchStatus = $"批量提交完成：成功 {submitted} 条，待提交 {BatchTasks.Count(task => task.Status == "待提交")} 条";
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

    private bool CanControlSelectedAgv() => SelectedAgv is { Online: true, CurrentTaskId: not null };

    private async Task ExecuteAgvCommandAsync(string command)
    {
        if (SelectedAgv is not { } agv || agv.CurrentTaskId is not { } taskId) return;
        await _mes.ExecuteAgvCommandAsync(agv.AgvId, command, taskId, _shutdown.Token);
        BatchStatus = $"已向 {agv.AgvId} 发送 {command} 指令";
        await RefreshAgvAsync();
        await RefreshAsync();
    }

    private async Task CreateTaskAsync()
    {
        var created = await _mes.CreateTaskAsync(_shutdown.Token);
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
