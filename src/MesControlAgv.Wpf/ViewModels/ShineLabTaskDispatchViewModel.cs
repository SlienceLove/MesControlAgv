using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Infrastructure;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Minimal high-level task sender for the confirmed ShineLab TCP contract.
/// It sends Config/Command through MES; it never opens a serial port or writes
/// an arbitrary frame.
/// </summary>
public sealed class ShineLabTaskDispatchViewModel : INotifyPropertyChanged
{
    private readonly IMesClient _mes;
    private string _equipmentCode = "SHA18I";
    private string _taskUuid = string.Empty;
    private string _sampleId = string.Empty;
    private string _sampleName = string.Empty;
    private string _sampleType = "1";
    private string _mPos = string.Empty;
    private string _channel = "A";
    private string _instrumentMethod = string.Empty;
    private string _processingMethod = string.Empty;
    private string _detectionMethod = string.Empty;
    private string _injectionVolume = string.Empty;
    private string _action = "0";
    private string _cleanTime = string.Empty;
    private int? _position = 1;
    private string _message = "实验阶段：Config 和 Command 会经由 MES 发送给 ShineLab。";
    private ShineLabTaskResponse? _lastTask;
    private bool _isBusy;

    public ShineLabTaskDispatchViewModel(IMesClient mes)
    {
        _mes = mes;
        TaskUuid = $"task-{DateTime.Now:yyyyMMdd-HHmmss}";
        SendConfigCommand = new AsyncCommand(SendConfigAsync, () => !IsBusy);
        SendCommandCommand = new AsyncCommand(SendCommandAsync, () => !IsBusy);
        RefreshTasksCommand = new AsyncCommand(RefreshTasksAsync, () => !IsBusy);
    }

    public ObservableCollection<ShineLabTaskResponse> Tasks { get; } = [];

    public string EquipmentCode { get => _equipmentCode; set => SetField(ref _equipmentCode, value); }
    public string TaskUuid { get => _taskUuid; set => SetField(ref _taskUuid, value); }
    public string SampleId { get => _sampleId; set => SetField(ref _sampleId, value); }
    public string SampleName { get => _sampleName; set => SetField(ref _sampleName, value); }
    public string SampleType { get => _sampleType; set => SetField(ref _sampleType, value); }
    public int? Position { get => _position; set => SetField(ref _position, value); }
    public string MPos { get => _mPos; set => SetField(ref _mPos, value); }
    public string Channel { get => _channel; set => SetField(ref _channel, value); }
    public string InstrumentMethod { get => _instrumentMethod; set => SetField(ref _instrumentMethod, value); }
    public string ProcessingMethod { get => _processingMethod; set => SetField(ref _processingMethod, value); }
    public string DetectionMethod { get => _detectionMethod; set => SetField(ref _detectionMethod, value); }
    public string InjectionVolume { get => _injectionVolume; set => SetField(ref _injectionVolume, value); }
    public string Action { get => _action; set => SetField(ref _action, value); }
    public string CleanTime { get => _cleanTime; set => SetField(ref _cleanTime, value); }
    public string Message { get => _message; private set => SetField(ref _message, value); }
    public ShineLabTaskResponse? LastTask { get => _lastTask; private set => SetField(ref _lastTask, value); }
    public string LastResponseText => LastTask is null
        ? "-"
        : $"{LastTask.TaskUuid}: {LastTask.Status} / {LastTask.CurrentStage} {LastTask.LastError}";
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            (SendConfigCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (SendCommandCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (RefreshTasksCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        }
    }

    public ICommand SendConfigCommand { get; }
    public ICommand SendCommandCommand { get; }
    public ICommand RefreshTasksCommand { get; }

    public async Task SendConfigAsync()
    {
        IsBusy = true;
        try
        {
            var row = new ShineLabSampleData(
                Require(SampleId, nameof(SampleId)),
                Require(SampleName, nameof(SampleName)),
                string.IsNullOrWhiteSpace(SampleType) ? "1" : SampleType.Trim(),
                Position ?? throw new ArgumentException("Position is required.", nameof(Position)),
                string.IsNullOrWhiteSpace(MPos) ? null : MPos.Trim(),
                Require(Channel, nameof(Channel)),
                Optional(InstrumentMethod),
                Optional(ProcessingMethod),
                Optional(DetectionMethod),
                ParseVolume(InjectionVolume),
                string.IsNullOrWhiteSpace(InjectionVolume) ? null : "uL");
            var equipment = Require(EquipmentCode, nameof(EquipmentCode));
            var taskUuid = Require(TaskUuid, nameof(TaskUuid));
            LastTask = await _mes.CreateShineLabTaskAsync(
                new ShineLabTaskCreateRequest(
                    equipment,
                    taskUuid,
                    [row],
                    Optional(InstrumentMethod),
                    Optional(ProcessingMethod),
                    Optional(DetectionMethod)),
                CancellationToken.None);
            Upsert(LastTask);
            LastTask = await _mes.ConfigureShineLabTaskAsync(taskUuid, CancellationToken.None);
            OnPropertyChanged(nameof(LastResponseText));
            Upsert(LastTask);
            Message = LastTask.Status == "Configured"
                ? "任务已持久化，Config 已被 ShineLab 接收。"
                : $"Config 结果：{LastTask.Status} / {LastTask.LastError}";
        }
        catch (Exception exception)
        {
            Message = $"Config 下发失败：{exception.Message}";
        }
        finally { IsBusy = false; }
    }

    public async Task SendCommandAsync()
    {
        IsBusy = true;
        try
        {
            if (!int.TryParse(Action, out var action))
                throw new ArgumentException("Action 必须是整数。", nameof(Action));
            var taskUuid = Require(TaskUuid, nameof(TaskUuid));
            LastTask = await _mes.SendShineLabTaskCommandAsync(
                taskUuid,
                new ShineLabCommandRequest(
                    taskUuid,
                    action,
                    Optional(SampleId),
                    Optional(SampleName),
                    Optional(Channel),
                    Optional(DetectionMethod),
                    Optional(CleanTime)),
                CancellationToken.None);
            OnPropertyChanged(nameof(LastResponseText));
            Upsert(LastTask);
            Message = LastTask.Status is "Accepted" or "Stopping"
                ? "Command 已被 ShineLab 接收并记录。"
                : $"Command 结果：{LastTask.Status} / {LastTask.LastError}";
        }
        catch (Exception exception)
        {
            Message = $"Command 下发失败：{exception.Message}";
        }
        finally { IsBusy = false; }
    }

    public async Task RefreshTasksAsync()
    {
        IsBusy = true;
        try
        {
            var tasks = await _mes.GetShineLabTasksAsync(100, CancellationToken.None);
            Tasks.Clear();
            foreach (var task in tasks) Tasks.Add(task);
            Message = $"已刷新 {Tasks.Count} 条 ShineLab 任务。";
        }
        catch (Exception exception)
        {
            Message = $"任务刷新失败：{exception.Message}";
        }
        finally { IsBusy = false; }
    }

    private void Upsert(ShineLabTaskResponse task)
    {
        var existing = Tasks.FirstOrDefault(item => item.TaskUuid == task.TaskUuid);
        if (existing is not null) Tasks.Remove(existing);
        Tasks.Insert(0, task);
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} 不能为空。", name) : value.Trim();

    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal? ParseVolume(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var volume)
                ? volume
                : throw new ArgumentException("InjectionVolume 必须是数字。", nameof(value));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;
}
