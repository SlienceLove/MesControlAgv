using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Aggregates AGV fleet state for the AGV-communication module.
/// AGV commands remain in <see cref="MainViewModel"/> during the first split step.
/// </summary>
public sealed class AgvCommunicationViewModel : INotifyPropertyChanged
{
    private AgvRowViewModel? _selectedAgv;
    private string _agvStatus = "鏈煡";
    private string _agvStation = "-";

    public ObservableCollection<AgvRowViewModel> Agvs { get; } = [];

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

