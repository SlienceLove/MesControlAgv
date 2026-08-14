using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>
/// 只读展示配置地图、实时预检和已分配的执行路径，不发送设备命令。
/// </summary>
public sealed class ReadinessViewModel : INotifyPropertyChanged
{
    private readonly IMesClient _mes;
    private readonly IMapLayoutSource _mapLayoutSource;
    private DashboardMapSnapshot? _mapSnapshot;
    private PhysicalAgvPreflightResponse? _preflight;
    private IReadOnlyList<AgvFleetDashboardStatus> _fleetStatus = [];
    private string _status = "尚未刷新";
    private bool _isRefreshing;
    private bool _mapLayoutAttempted;
    private string? _mapLayoutError;
    private MapLayoutResult? _mapLayoutResult;
    private MapIdentityVerificationResult? _mapIdentityVerification;

    public ReadinessViewModel(IMesClient mes, IMapLayoutSource? mapLayoutSource = null)
    {
        _mes = mes ?? throw new ArgumentNullException(nameof(mes));
        _mapLayoutSource = mapLayoutSource ?? EmptyMapLayoutSource.Instance;
        RefreshCommand = new AsyncCommand(() => RefreshAsync(), () => !IsRefreshing);
        LoadMapCommand = new AsyncCommand(() => Task.CompletedTask, () => !IsRefreshing);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand RefreshCommand { get; }
    public ICommand LoadMapCommand { get; }

    public MapViewModel Map { get; } = new();
    public MapViewportViewModel Viewport { get; } = new();

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
            VerifyMapLayoutIdentity();
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

    public string? MapLayoutError
    {
        get => _mapLayoutError;
        private set => SetField(ref _mapLayoutError, value);
    }

    public string MapLayoutVerificationStatus
    {
        get
        {
            if (_mapLayoutResult is null || !_mapLayoutResult.Loaded)
            {
                return MapLayoutError is null
                    ? "未配置 .smap，使用 MES 自动布局"
                    : "地图布局加载失败，使用 MES 自动布局";
            }

            return _mapIdentityVerification?.Status switch
            {
                MapIdentityVerificationStatus.Match => "已验证一致",
                MapIdentityVerificationStatus.Mismatch => "不一致，已禁用运行叠加",
                _ => "无法验证，已禁用运行叠加"
            };
        }
    }

    public string MapLayoutVerificationDetails => _mapIdentityVerification is null
        ? MapLayoutError ?? "未加载独立 .smap 布局。"
        : string.Join("；", _mapIdentityVerification.Reasons);

    public bool IsMapLayoutVerified =>
        _mapIdentityVerification?.Status == MapIdentityVerificationStatus.Match;

    public bool DispatchPermitted => Preflight?.DispatchPermitted == true;

    public string BlockingReasons => Preflight is null
        ? "物理预检尚未加载。"
        : Preflight.BlockingReasons.Count == 0
            ? "当前驱动未报告阻断原因。"
            : string.Join("；", Preflight.BlockingReasons);

    public string OperatingMode => Preflight?.Readiness?.VehicleOperatingMode?.ToLowerInvariant() switch
    {
        "automatic" => "自动",
        "manual" => "手动",
        null or "" or "unknown" => "未知",
        var mode => mode
    };

    public string ProfileFingerprint
    {
        get
        {
            if (MapSnapshot is null) return "配置地图：未知";
            var name = MapSnapshot.ProfileMapName ?? "未知";
            var version = MapSnapshot.ProfileMapVersion ?? "未知";
            var md5 = MapSnapshot.ProfileMapMd5 ?? "未知";
            return $"配置地图：{name} / {version} / {md5}";
        }
    }

    public string LiveFingerprint
    {
        get
        {
            var readiness = Preflight?.Readiness;
            if (readiness is null) return "实时地图：当前驱动未提供";
            return $"实时地图：{readiness.MapName ?? "未知"} / {readiness.MapMd5 ?? "未知"}";
        }
    }

    public string MapStationSummary => MapSnapshot is null
        ? "地图站点：未加载"
        : $"地图站点（{MapSnapshot.Stations.Count}）：{string.Join("、", MapSnapshot.Stations.Select(item => item.AgvStationId))}";

    public string MapEdgeSummary => MapSnapshot is null
        ? "地图边：未加载"
        : $"有向/配置边（{MapSnapshot.Edges.Count}）：{string.Join("、", MapSnapshot.Edges.Select(edge => $"{edge.From} -> {edge.To}"))}";

    public string ActualExecutionPath
    {
        get
        {
            var paths = FleetStatus
                .Where(item => item.ActiveTask?.Path is { Count: > 0 })
                .Select(item => $"{item.Snapshot.AgvId}: {string.Join(" -> ", item.ActiveTask!.Path!)}")
                .ToArray();
            return paths.Length == 0 ? "暂无活动执行路径" : string.Join("；", paths);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        Status = "正在刷新只读就绪数据...";
        try
        {
            var mapTask = _mes.GetMapSnapshotAsync(cancellationToken);
            var preflightTask = _mes.GetPhysicalPreflightAsync(cancellationToken);
            var layoutTask = LoadMapLayoutOnceAsync(cancellationToken);
            await Task.WhenAll(mapTask, preflightTask, layoutTask);
            MapSnapshot = await mapTask;
            Preflight = await preflightTask;
            Status = MapLayoutError is null
                ? $"只读快照已接收：{DateTimeOffset.UtcNow:O}"
                : $"只读快照已接收；地图布局加载失败：{MapLayoutError}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "就绪状态刷新已取消。";
        }
        catch (Exception exception)
        {
            Status = $"就绪状态刷新失败：{exception.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public void UpdateFleet(IReadOnlyList<AgvFleetDashboardStatus> statuses) => FleetStatus = statuses ?? [];

    public async Task LoadMapFromFileAsync(string smapFilePath, string? mappingFilePath = null, CancellationToken cancellationToken = default)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        Status = "正在加载 SMAP 地图文件...";
        _mapLayoutAttempted = false;
        _mapLayoutError = null;
        _mapLayoutResult = null;
        _mapIdentityVerification = null;

        try
        {
            var source = new SmapMapLayoutSource(
                () => smapFilePath,
                () => mappingFilePath);
            var result = await source.LoadAsync(cancellationToken);
            _mapLayoutResult = result;
            _mapLayoutAttempted = true;
            MapLayoutError = result.Error;

            if (result.Loaded && result.Layout is not null)
            {
                Map.ApplyLayout(result.Layout, result.Mapping, Map.CanvasWidth, Map.CanvasHeight);
                VerifyMapLayoutIdentity();
                Status = $"SMAP 地图已加载：{System.IO.Path.GetFileName(smapFilePath)}";
            }
            else
            {
                Status = $"SMAP 地图加载失败：{result.Error ?? "未知错误"}";
            }

            OnPropertyChanged(nameof(MapLayoutVerificationStatus));
            OnPropertyChanged(nameof(MapLayoutVerificationDetails));
            OnPropertyChanged(nameof(IsMapLayoutVerified));
            RefreshMap();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "地图加载已取消。";
        }
        catch (Exception exception)
        {
            Status = $"地图加载失败：{exception.Message}";
            MapLayoutError = exception.Message;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void RefreshMap() => Map.Update(MapSnapshot, Preflight, FleetStatus);

    private async Task LoadMapLayoutOnceAsync(CancellationToken cancellationToken)
    {
        if (_mapLayoutAttempted) return;
        _mapLayoutAttempted = true;
        var result = await _mapLayoutSource.LoadAsync(cancellationToken);
        _mapLayoutResult = result;
        MapLayoutError = result.Error;
        if (result.Loaded && result.Layout is not null)
        {
            Map.ApplyLayout(result.Layout, result.Mapping, Map.CanvasWidth, Map.CanvasHeight);
            VerifyMapLayoutIdentity();
        }

        OnPropertyChanged(nameof(MapLayoutVerificationStatus));
        OnPropertyChanged(nameof(MapLayoutVerificationDetails));
        OnPropertyChanged(nameof(IsMapLayoutVerified));
    }

    private void VerifyMapLayoutIdentity()
    {
        if (_mapLayoutResult is not { Loaded: true })
        {
            _mapIdentityVerification = null;
            return;
        }

        _mapIdentityVerification = MapLayoutIdentityVerifier.Verify(
            _mapLayoutResult.Identity,
            _mapLayoutResult.Mapping,
            MapSnapshot);
        Map.SetSmapOverlayAllowed(
            _mapIdentityVerification.Status == MapIdentityVerificationStatus.Match);
        OnPropertyChanged(nameof(MapLayoutVerificationStatus));
        OnPropertyChanged(nameof(MapLayoutVerificationDetails));
        OnPropertyChanged(nameof(IsMapLayoutVerified));
    }

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

    private sealed class EmptyMapLayoutSource : IMapLayoutSource
    {
        public static EmptyMapLayoutSource Instance { get; } = new();

        public Task<MapLayoutResult> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(new MapLayoutResult(null, MesControlAgv.Domain.Map.StationMappingConfig.Empty, Loaded: false, Error: null));
    }
}
