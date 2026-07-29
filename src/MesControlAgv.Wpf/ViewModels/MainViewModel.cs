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
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(2));
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _refreshLoop;
    private TaskRowViewModel? _selectedTask;
    private string _connectionStatus = "正在连接 MES";
    private string _agvStatus = "未知";
    private string _agvStation = "-";
    private string _message = string.Empty;

    public MainViewModel(IMesClient mes)
    {
        _mes = mes;
        CreateTaskCommand = new AsyncCommand(CreateTaskAsync);
        ArriveCommand = new AsyncCommand(ArriveAsync, () => SelectedTask?.Status is "MovingToPickup" or "MovingToDropoff");
        ConfirmPickupCommand = new AsyncCommand(ConfirmPickupAsync, () => SelectedTask?.Status == "WaitingPickupConfirmation");
        ConfirmDropoffCommand = new AsyncCommand(ConfirmDropoffAsync, () => SelectedTask?.Status == "WaitingDropoffConfirmation");
        RetryCommand = new AsyncCommand(RetryAsync, () => SelectedTask?.Status == "Failed");
        CancelCommand = new AsyncCommand(CancelAsync, () => SelectedTask is { Status: not "Completed" and not "Cancelled" });
    }

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];

    public TaskRowViewModel? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (!SetField(ref _selectedTask, value)) return;
            RefreshCommandState();
        }
    }

    public string ConnectionStatus { get => _connectionStatus; private set => SetField(ref _connectionStatus, value); }
    public string AgvStatus { get => _agvStatus; private set => SetField(ref _agvStatus, value); }
    public string AgvStation { get => _agvStation; private set => SetField(ref _agvStation, value); }
    public string Message { get => _message; private set => SetField(ref _message, value); }

    public ICommand CreateTaskCommand { get; }
    public ICommand ArriveCommand { get; }
    public ICommand ConfirmPickupCommand { get; }
    public ICommand ConfirmDropoffCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CancelCommand { get; }

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
            SelectedTask = Tasks.SingleOrDefault(task => task.Id == selectedId) ?? Tasks.FirstOrDefault();
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

    private async Task CancelAsync()
    {
        if (SelectedTask is not null) await _mes.CancelAsync(SelectedTask.Id, "wpf-operator", _shutdown.Token);
        await RefreshAsync();
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
        foreach (var command in new[] { ArriveCommand, ConfirmPickupCommand, ConfirmDropoffCommand, RetryCommand, CancelCommand }.OfType<AsyncCommand>()) command.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
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
