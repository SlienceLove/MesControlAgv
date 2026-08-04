using System.ComponentModel;
using System.Runtime.CompilerServices;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class AgvRowViewModel(AgvDashboardSnapshot snapshot) : INotifyPropertyChanged
{
    private AgvDashboardSnapshot _snapshot = snapshot;

    public string AgvId => _snapshot.AgvId;
    public bool Online => _snapshot.Online;
    public string OnlineText => Online ? "在线" : "离线";
    public string ControlOwner => string.IsNullOrWhiteSpace(_snapshot.ControlOwner) ? "-" : _snapshot.ControlOwner;
    public string CurrentStationId => _snapshot.CurrentStationId ?? "-";
    public Guid? CurrentTaskId => _snapshot.CurrentTaskId;
    public string CurrentTaskText => CurrentTaskId?.ToString() ?? "空闲";

    public void Update(AgvDashboardSnapshot snapshot)
    {
        _snapshot = snapshot;
        OnPropertyChanged(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class BatchTaskRowViewModel(BatchTaskImportItem item) : INotifyPropertyChanged
{
    public int SourceRowNumber { get; } = item.SourceRowNumber;
    public string TaskId { get; } = item.TaskId;
    public string SourceStation { get; } = item.SourceStation;
    public string TargetStation { get; } = item.TargetStation;
    public string Description { get; } = item.Description;
    public DateTime? PlannedTime { get; } = item.PlannedTime;

    private int _priority = item.Priority;
    private string _status = "待提交";

    public int Priority { get => _priority; set { if (_priority == value) return; _priority = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Priority))); } }
    public string Status { get => _status; private set { if (_status == value) return; _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }

    public void MarkSubmitted() => Status = "已提交";
    public void MarkFailed(string message) => Status = $"失败：{message}";
    public BatchTaskImportItem ToImportItem() => new(SourceRowNumber, TaskId, SourceStation, TargetStation, Description, Priority, PlannedTime);

    public event PropertyChangedEventHandler? PropertyChanged;
}
