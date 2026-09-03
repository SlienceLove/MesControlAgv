using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// 汇总 AGV 通讯模块的车队状态。
/// 第一阶段拆分中，AGV 命令仍由 <see cref="MainViewModel"/> 负责。
/// </summary>
public sealed class AgvCommunicationViewModel : INotifyPropertyChanged
{
    private AgvRowViewModel? _selectedAgv;
    private string _agvStatus = "未知";
    private string _agvStation = "-";
    private string _agvExecutionStatus = "无活动运输任务";
    private RuntimeConnectionSource _connectionSource = RuntimeConnectionSource.Unverified;

    public ObservableCollection<AgvRowViewModel> Agvs { get; } = [];

    public void UpdateFleet(IReadOnlyList<AgvFleetDashboardStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var selectedId = SelectedAgv?.AgvId;
        var byId = Agvs.ToDictionary(row => row.AgvId, StringComparer.Ordinal);
        foreach (var status in statuses)
        {
            if (byId.TryGetValue(status.Snapshot.AgvId, out var row))
            {
                row.SetConnectionSource(ConnectionSource);
                row.Update(status);
            }
            else Agvs.Add(new AgvRowViewModel(status, ConnectionSource));
        }

        foreach (var row in Agvs
                     .Where(row => statuses.All(status => status.Snapshot.AgvId != row.AgvId))
                     .ToList())
        {
            Agvs.Remove(row);
        }

        SelectedAgv = Agvs.FirstOrDefault(row => row.AgvId == selectedId) ?? Agvs.FirstOrDefault();
    }

    public RuntimeConnectionSource ConnectionSource => _connectionSource;
    public string ConnectionSourceDisplay => RuntimeConnectionSourcePresentation.SourceDisplay(ConnectionSource);
    public string ConnectionSourceDetail => RuntimeConnectionSourcePresentation.SourceDetail(ConnectionSource);
    public string ConnectionSourceBrush => RuntimeConnectionSourcePresentation.SourceBrush(ConnectionSource);
    public string ConnectionSourceForeground => RuntimeConnectionSourcePresentation.SourceForeground(ConnectionSource);

    public void ConfigureRuntimeMode(string? runtimeMode)
    {
        var source = RuntimeConnectionSourcePresentation.Resolve(runtimeMode);
        if (_connectionSource == source) return;
        _connectionSource = source;
        foreach (var row in Agvs) row.SetConnectionSource(source);
        OnPropertyChanged(nameof(ConnectionSource));
        OnPropertyChanged(nameof(ConnectionSourceDisplay));
        OnPropertyChanged(nameof(ConnectionSourceDetail));
        OnPropertyChanged(nameof(ConnectionSourceBrush));
        OnPropertyChanged(nameof(ConnectionSourceForeground));
    }

    public string DescribePrimaryConnection(AgvDashboardSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return ConnectionSource switch
            {
                RuntimeConnectionSource.LocalSimulator => "本地模拟器：无 AGV 数据",
                RuntimeConnectionSource.PhysicalDevice => "物理设备未验证（无 AGV 数据）",
                _ => "来源未验证：无 AGV 数据"
            };
        }

        var connection = RuntimeConnectionSourcePresentation.DescribeConnection(
            ConnectionSource,
            snapshot.Online);
        return snapshot.Online && !string.IsNullOrWhiteSpace(snapshot.ControlOwner)
            ? $"{connection} / {snapshot.ControlOwner}"
            : connection;
    }

    public AgvRowViewModel? SelectedAgv
    {
        get => _selectedAgv;
        set => SetField(ref _selectedAgv, value);
    }

    public string AgvStatus
    {
        get => _agvStatus;
        set => SetField(ref _agvStatus, value);
    }

    public string AgvStation
    {
        get => _agvStation;
        set => SetField(ref _agvStation, value);
    }

    public string AgvExecutionStatus
    {
        get => _agvExecutionStatus;
        set => SetField(ref _agvExecutionStatus, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
