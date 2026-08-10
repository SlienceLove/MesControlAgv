using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// Read-only operator view of the configured map, live preflight and assigned
/// execution paths. It never sends a device command.
/// </summary>
public sealed class ReadinessViewModel : INotifyPropertyChanged
{
    private readonly IMesClient _mes;
    private DashboardMapSnapshot? _mapSnapshot;
    private PhysicalAgvPreflightResponse? _preflight;
    private IReadOnlyList<AgvFleetDashboardStatus> _fleetStatus = [];
    private string _status = "Not refreshed";
    private bool _isRefreshing;

    public ReadinessViewModel(IMesClient mes)
    {
        _mes = mes ?? throw new ArgumentNullException(nameof(mes));
        RefreshCommand = new AsyncCommand(() => RefreshAsync(), () => !IsRefreshing);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand RefreshCommand { get; }

    public MapViewModel Map { get; } = new();

    public DashboardMapSnapshot? MapSnapshot
    {
        get => _mapSnapshot;
        private set
        {
            if (ReferenceEquals(_mapSnapshot, value)) return;
            _mapSnapshot = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProfileFingerprint));
            OnPropertyChanged(nameof(MapStationSummary));
            OnPropertyChanged(nameof(MapEdgeSummary));
            RefreshMap();
        }
    }

    public PhysicalAgvPreflightResponse? Preflight
    {
        get => _preflight;
        private set
        {
            if (ReferenceEquals(_preflight, value)) return;
            _preflight = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DispatchPermitted));
            OnPropertyChanged(nameof(BlockingReasons));
            OnPropertyChanged(nameof(LiveFingerprint));
            OnPropertyChanged(nameof(OperatingMode));
            RefreshMap();
        }
    }

    public IReadOnlyList<AgvFleetDashboardStatus> FleetStatus
    {
        get => _fleetStatus;
        private set
        {
            _fleetStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActualExecutionPath));
            RefreshMap();
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (_isRefreshing == value) return;
            _isRefreshing = value;
            OnPropertyChanged();
            if (RefreshCommand is AsyncCommand command) command.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public bool DispatchPermitted => Preflight?.DispatchPermitted == true;

    public string BlockingReasons => Preflight is null
        ? "Physical preflight has not been loaded."
        : Preflight.BlockingReasons.Count == 0
            ? "None reported by the active driver."
            : string.Join("; ", Preflight.BlockingReasons);

    public string OperatingMode => Preflight?.Readiness?.VehicleOperatingMode ?? "unknown";

    public string ProfileFingerprint
    {
        get
        {
            if (MapSnapshot is null) return "Profile map: unknown";
            var name = MapSnapshot.ProfileMapName ?? "unknown";
            var version = MapSnapshot.ProfileMapVersion ?? "unknown";
            var md5 = MapSnapshot.ProfileMapMd5 ?? "unknown";
            return $"Profile map: {name} / {version} / {md5}";
        }
    }

    public string LiveFingerprint
    {
        get
        {
            var readiness = Preflight?.Readiness;
            if (readiness is null) return "Live map: not supplied by active driver";
            return $"Live map: {readiness.MapName ?? "unknown"} / {readiness.MapMd5 ?? "unknown"}";
        }
    }

    public string MapStationSummary => MapSnapshot is null
        ? "Map stations: not loaded"
        : $"Map stations ({MapSnapshot.Stations.Count}): {string.Join(", ", MapSnapshot.Stations.Select(item => item.AgvStationId))}";

    public string MapEdgeSummary => MapSnapshot is null
        ? "Map edges: not loaded"
        : $"Directed/configured edges ({MapSnapshot.Edges.Count}): {string.Join(", ", MapSnapshot.Edges.Select(edge => $"{edge.From} -> {edge.To}"))}";

    public string ActualExecutionPath
    {
        get
        {
            var paths = FleetStatus
                .Where(item => item.ActiveTask?.Path is { Count: > 0 })
                .Select(item => $"{item.Snapshot.AgvId}: {string.Join(" -> ", item.ActiveTask!.Path!)}")
                .ToArray();
            return paths.Length == 0 ? "No active execution path reported." : string.Join(" | ", paths);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        Status = "Refreshing read-only readiness data...";
        try
        {
            var mapTask = _mes.GetMapSnapshotAsync(cancellationToken);
            var preflightTask = _mes.GetPhysicalPreflightAsync(cancellationToken);
            await Task.WhenAll(mapTask, preflightTask);
            MapSnapshot = await mapTask;
            Preflight = await preflightTask;
            Status = $"Read-only snapshot received at {DateTimeOffset.UtcNow:O}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Readiness refresh cancelled.";
        }
        catch (Exception exception)
        {
            Status = $"Readiness refresh failed: {exception.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public void UpdateFleet(IReadOnlyList<AgvFleetDashboardStatus> statuses) => FleetStatus = statuses ?? [];

    private void RefreshMap() => Map.Update(MapSnapshot, Preflight, FleetStatus);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        private readonly Func<Task> _execute = execute;
        private readonly Func<bool> _canExecute = canExecute;
        private bool _running;

        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => !_running && _canExecute();

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            _running = true;
            RaiseCanExecuteChanged();
            try { await _execute(); }
            finally
            {
                _running = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
