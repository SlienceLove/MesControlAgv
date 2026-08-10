using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Aggregates task-list state for the task-monitor module.
/// Task actions remain in <see cref="MainViewModel"/> during the first split step.
/// </summary>
public sealed class TaskMonitorViewModel : INotifyPropertyChanged
{
    private TaskRowViewModel? _selectedTask;
    private string _connectionStatus = "正在连接 MES";
    private DateTime? _taskFilterDate = DateTime.UtcNow.Date;

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];
    public ObservableCollection<TaskEventRowViewModel> TaskEvents { get; } = [];

    public TaskRowViewModel? SelectedTask
    {
        get => _selectedTask;
        set => SetField(ref _selectedTask, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => SetField(ref _connectionStatus, value);
    }

    public DateTime? TaskFilterDate
    {
        get => _taskFilterDate;
        set
        {
            if (value is not { } date) return;
            SetField(ref _taskFilterDate, date.Date);
        }
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

