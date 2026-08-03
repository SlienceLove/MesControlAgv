using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IMesClient _mes;
    private readonly ISimulatorControlClient? _simulator;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(2));
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _detailRefresh;
    private Task? _refreshLoop;
    private TaskRowViewModel? _selectedTask;
    private bool _suppressDetailRefresh;
    private string _connectionStatus = "正在连接 MES";
    private string _agvStatus = "未知";
    private string _agvStation = "-";
    private string _message = string.Empty;

    public MainViewModel(IMesClient mes, ISimulatorControlClient? simulator = null)
    {
        _mes = mes;
        _simulator = simulator;
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
    }

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];
    public ObservableCollection<TaskEventRowViewModel> TaskEvents { get; } = [];

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

    public string ConnectionStatus { get => _connectionStatus; private set => SetField(ref _connectionStatus, value); }
    public string AgvStatus { get => _agvStatus; private set => SetField(ref _agvStatus, value); }
    public string AgvStation { get => _agvStation; private set => SetField(ref _agvStation, value); }
    public string Message { get => _message; private set => SetField(ref _message, value); }

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

    public async Task StartAsync()
    {
        await RefreshAsync();
        _refreshLoop = RefreshLoopAsync(_shutdown.Token);
    }

    public async Task RefreshAsync()
    {
        try
        {
            var tasks = await _mes.GetTasksAsync(_shutdown.Token);
            var snapshot = await _mes.GetAgvSnapshotAsync(_shutdown.Token);
            var selectedId = SelectedTask?.Id;
            Tasks.Clear();
            foreach (var task in tasks) Tasks.Add(TaskRowViewModel.From(task));
            CancelPendingDetailRefresh();
            _suppressDetailRefresh = true;
            try
            {
                SelectedTask = Tasks.SingleOrDefault(task => task.Id == selectedId) ?? Tasks.FirstOrDefault();
            }
            finally
            {
                _suppressDetailRefresh = false;
            }
            await LoadTaskDetailAsync(SelectedTask?.Id, _shutdown.Token);
            ConnectionStatus = "MES 已连接";
            AgvStatus = snapshot.Online ? $"在线 / {snapshot.ControlOwner}" : "离线";
            AgvStation = snapshot.CurrentStationId ?? "移动中";
            Message = string.Empty;
        }
        catch (Exception exception)
        {
            ConnectionStatus = "MES 不可用";
            Message = exception.Message;
        }
    }

    private async Task CreateTaskAsync()
    {
        await _mes.CreateTaskAsync(_shutdown.Token);
        await RefreshAsync();
    }

    private async Task ArriveAsync()
    {
        if (SelectedTask is not null) await _mes.MarkArrivedAsync(SelectedTask.Id, _shutdown.Token);
        await RefreshAsync();
    }

    private async Task ConfirmPickupAsync()
    {
        if (SelectedTask is not null) await _mes.ConfirmPickupAsync(SelectedTask.Id, "wpf-operator", _shutdown.Token);
        await RefreshAsync();
    }

    private async Task ConfirmDropoffAsync()
    {
        if (SelectedTask is not null) await _mes.ConfirmDropoffAsync(SelectedTask.Id, "wpf-operator", _shutdown.Token);
        await RefreshAsync();
    }

    private async Task RetryAsync()
    {
        if (SelectedTask is not null) await _mes.RetryAsync(SelectedTask.Id, _shutdown.Token);
        await RefreshAsync();
    }

    private async Task RecoverAsync()
    {
        if (SelectedTask is not null) await _mes.RecoverAsync(SelectedTask.Id, _shutdown.Token);
        await RefreshAsync();
    }

    private async Task CancelAsync()
    {
        if (SelectedTask is not null) await _mes.CancelAsync(SelectedTask.Id, "wpf-operator", _shutdown.Token);
        await RefreshAsync();
    }

    private async Task ApplySimulatorControlAsync(string mode)
    {
        if (_simulator is null) return;
        await _simulator.ApplyControlAsync(mode, _shutdown.Token);
        await RefreshAsync();
    }

    private async Task ExecuteActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            Message = exception.Message;
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(cancellationToken)) await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RefreshCommandState()
    {
        foreach (var command in new[] { ArriveCommand, ConfirmPickupCommand, ConfirmDropoffCommand, RetryCommand, RecoverCommand, CancelCommand }.OfType<AsyncCommand>()) command.RaiseCanExecuteChanged();
    }

    private void RequestTaskDetailRefresh()
    {
        CancelPendingDetailRefresh();
        _detailRefresh = new CancellationTokenSource();
        _ = RefreshSelectedTaskDetailAsync(SelectedTask?.Id, _detailRefresh.Token);
    }

    private async Task RefreshSelectedTaskDetailAsync(Guid? taskId, CancellationToken cancellationToken)
    {
        try
        {
            await LoadTaskDetailAsync(taskId, cancellationToken);
            if (taskId == SelectedTask?.Id) Message = string.Empty;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Message = exception.Message;
        }
    }

    private async Task LoadTaskDetailAsync(Guid? taskId, CancellationToken cancellationToken)
    {
        TaskEvents.Clear();
        if (taskId is not { } id) return;

        var detail = await _mes.GetTaskDetailAsync(id, cancellationToken);
        if (detail is null || id != SelectedTask?.Id) return;
        foreach (var taskEvent in detail.Events) TaskEvents.Add(TaskEventRowViewModel.From(taskEvent));
    }

    private void CancelPendingDetailRefresh()
    {
        _detailRefresh?.Cancel();
        _detailRefresh?.Dispose();
        _detailRefresh = null;
    }

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
