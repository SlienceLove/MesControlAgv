using System.ComponentModel;
using System.Runtime.CompilerServices;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed class AgvRowViewModel(AgvDashboardSnapshot snapshot) : INotifyPropertyChanged
{
    private AgvDashboardSnapshot _snapshot = snapshot;

    public string AgvId => _snapshot.AgvId;
    public bool Online => _snapshot.Online;
    public string OnlineText => Online ? "\u5728\u7EBF" : "\u79BB\u7EBF";
    public string ControlOwner => string.IsNullOrWhiteSpace(_snapshot.ControlOwner) ? "-" : _snapshot.ControlOwner;
    public string CurrentStationId => _snapshot.CurrentStationId ?? "-";
    public Guid? CurrentTaskId => _snapshot.CurrentTaskId;
    public string CurrentTaskText => CurrentTaskId?.ToString() ?? "\u7A7A\u95F2";
    public AgvCapabilitiesResponse Capabilities => _snapshot.Capabilities ?? AgvCapabilitiesResponse.Standard;
    public bool SupportsPause => Capabilities.SupportsPause;
    public bool SupportsResume => Capabilities.SupportsResume;
    public bool SupportsCancel => Capabilities.SupportsCancel;
    public string CapabilitySummary => string.Join(" / ", new[]
    {
        SupportsPause ? "\u6682\u505C" : null,
        SupportsResume ? "\u6062\u590D" : null,
        SupportsCancel ? "\u53D6\u6D88" : null,
        Capabilities.SupportsLift ? "\u5347\u964D" : null,
        Capabilities.SupportsBarcode ? "\u6761\u7801" : null
    }.Where(item => item is not null)) switch
    {
        { Length: > 0 } value => value,
        _ => "\u65E0\u989D\u5916\u80FD\u529B"
    };

    public bool Supports(string command) => command.Trim().ToLowerInvariant() switch
    {
        "pause" => SupportsPause,
        "resume" => SupportsResume,
        "cancel" => SupportsCancel,
        _ => false
    };

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
    private string _status = "\u5F85\u63D0\u4EA4";

    public int Priority { get => _priority; set { if (_priority == value) return; _priority = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Priority))); } }
    public string Status { get => _status; private set { if (_status == value) return; _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }

    public void MarkSubmitted() => Status = "\u5DF2\u63D0\u4EA4";
    public void MarkFailed(string message) => Status = $"\u5931\u8D25\uFF1A{message}";
    public BatchTaskImportItem ToImportItem() => new(SourceRowNumber, TaskId, SourceStation, TargetStation, Description, Priority, PlannedTime);

    public event PropertyChangedEventHandler? PropertyChanged;
}
