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

    public ObservableCollection<AgvRowViewModel> Agvs { get; } = [];

    public void UpdateFleet(IReadOnlyList<AgvFleetDashboardStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var selectedId = SelectedAgv?.AgvId;
        var byId = Agvs.ToDictionary(row => row.AgvId, StringComparer.Ordinal);
        foreach (var status in statuses)
        {
            if (byId.TryGetValue(status.Snapshot.AgvId, out var row)) row.Update(status);
            else Agvs.Add(new AgvRowViewModel(status));
        }

        foreach (var row in Agvs
                     .Where(row => statuses.All(status => status.Snapshot.AgvId != row.AgvId))
                     .ToList())
        {
            Agvs.Remove(row);
        }

        SelectedAgv = Agvs.FirstOrDefault(row => row.AgvId == selectedId) ?? Agvs.FirstOrDefault();
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

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
