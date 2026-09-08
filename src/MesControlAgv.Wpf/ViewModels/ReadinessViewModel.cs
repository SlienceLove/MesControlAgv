using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Infrastructure;
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
    private PhysicalReadinessResponse? _supervisorReadiness;
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
    public OfflineDataStateViewModel OfflineState { get; } = new();

    public MapViewModel Map { get; } = new();
    public MapViewportViewModel Viewport { get; } = new();

    /// <summary>
    /// Station catalog derived from the local SMAP layout and station mapping.
    /// It remains available when MES is offline, so the workflow editor can
    /// avoid falling back to the legacy CHARGE_01/SAMPLE_01 defaults.
    /// </summary>
    public IReadOnlyList<DashboardStation> LocalStationCatalog
    {
        get
        {
            if (_mapLayoutResult is not { Loaded: true, Layout: { } layout })
                return [];

            return layout.Stations
                .Select((station, index) =>
                {
                    var mapped = _mapLayoutResult.Mapping.TryResolve(station.Id, out var entry)
                        ? entry
                        : null;
                    var agvStationId = string.IsNullOrWhiteSpace(mapped?.MesAgvStationId)
                        ? station.Id
                        : mapped!.MesAgvStationId!;
                    var code = ParseStationCode(agvStationId, index + 1);
                    return new DashboardStation(
                        code,
                        string.IsNullOrWhiteSpace(mapped?.DisplayName) ? agvStationId : mapped!.DisplayName!,
                        agvStationId,
                        true,
                        code == 1 ? "Charge" : "FieldAcceptance");
                })
                .OrderBy(station => station.Code)
                .ToArray();
        }
    }

    /// <summary>Directed routes recovered from the local SMAP for offline route selection.</summary>
    public IReadOnlyList<MapEdgeResponse> LocalMapEdges =>
        _mapLayoutResult is { Loaded: true, Layout: { } layout }
            ? layout.Routes
                .Select(route => new MapEdgeResponse(
                    ResolveMappedStationId(route.FromStationId),
                    ResolveMappedStationId(route.ToStationId),
                    1,
                    false))
                .DistinctBy(edge => $"{edge.From}\u001f{edge.To}", StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

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

    /// <summary>
    /// Unified multi-device supervisor projection. It is separate from the
    /// legacy single-AGV preflight so an unavailable optional endpoint cannot
    /// erase the detailed map/safety evidence already shown below.
    /// </summary>
    public PhysicalReadinessResponse? SupervisorReadiness
    {
        get => _supervisorReadiness;
        private set
        {
            if (ReferenceEquals(_supervisorReadiness, value)) return;
            _supervisorReadiness = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DeviceReadiness));
            OnPropertyChanged(nameof(PhysicalSchedulingPermitted));
            OnPropertyChanged(nameof(SupervisorBlockingReasons));
            OnPropertyChanged(nameof(SupervisorStatus));
            OnPropertyChanged(nameof(DispatchPermitted));
        }
    }

    public IReadOnlyList<PhysicalDeviceReadinessSnapshot> DeviceReadiness =>
        SupervisorReadiness?.Devices ?? Array.Empty<PhysicalDeviceReadinessSnapshot>();

    public bool PhysicalSchedulingPermitted =>
        SupervisorReadiness is null ||
        !SupervisorReadiness.Enabled ||
        SupervisorReadiness.SchedulingPermitted;

    public string SupervisorBlockingReasons => SupervisorReadiness is null
        ? "物理设备监督器尚未返回数据。"
        : SupervisorReadiness.BlockingReasons.Count == 0
            ? "当前无监督器阻断原因。"
            : string.Join("；", SupervisorReadiness.BlockingReasons);

    public string SupervisorStatus => SupervisorReadiness is null
        ? "未连接监督器"
        : !SupervisorReadiness.Enabled
            ? "监督器未启用（不访问物理设备）"
            : $"{(SupervisorReadiness.SchedulingPermitted ? "可调度" : "不可调度")} / " +
              $"最近观测 {SupervisorReadiness.ObservedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

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

    public bool DispatchPermitted =>
        Preflight?.DispatchPermitted == true && PhysicalSchedulingPermitted;

    public string BlockingReasons => Preflight is null
        ? SupervisorReadiness is { Enabled: true } && SupervisorReadiness.BlockingReasons.Count > 0
            ? SupervisorBlockingReasons
            : "物理预检尚未加载。"
        : Preflight.BlockingReasons.Count == 0
            ? SupervisorReadiness is { Enabled: true } && SupervisorReadiness.BlockingReasons.Count > 0
                ? SupervisorBlockingReasons
                : "当前驱动未报告阻断原因。"
            : string.Join("；", Preflight.BlockingReasons) +
              (SupervisorReadiness is { Enabled: true } && SupervisorReadiness.BlockingReasons.Count > 0
                  ? $"；监督器：{SupervisorBlockingReasons}"
                  : string.Empty);

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
        OfflineState.BeginLoading("正在刷新只读就绪数据...");
        Status = "正在刷新只读就绪数据...";
        try
        {
            // Keep the independent reads concurrent. A disconnected MES or a
            // simulator that does not expose physical preflight must not make
            // the local map wait behind several sequential HTTP timeouts.
            var mapTask = ReadOptionalAsync(
                () => _mes.GetMapSnapshotAsync(cancellationToken),
                cancellationToken);
            var preflightTask = ReadOptionalAsync(
                () => _mes.GetPhysicalPreflightAsync(cancellationToken),
                cancellationToken);
            var supervisorTask = ReadOptionalAsync(
                () => _mes.RefreshPhysicalReadinessAsync(forceFull: true, cancellationToken),
                cancellationToken);
            var layoutTask = LoadMapLayoutOnceAsync(cancellationToken);
            await Task.WhenAll(mapTask, preflightTask, supervisorTask, layoutTask);

            var mapRead = await mapTask;
            var preflightRead = await preflightTask;
            var supervisorRead = await supervisorTask;
            if (mapRead.Value is not null) MapSnapshot = mapRead.Value;
            if (preflightRead.Value is not null) Preflight = preflightRead.Value;
            if (supervisorRead.Value is not null) SupervisorReadiness = supervisorRead.Value;

            var hasRemoteData = mapRead.Value is not null ||
                                preflightRead.Value is not null ||
                                supervisorRead.Value is not null;
            var hasLocalLayout = _mapLayoutResult is { Loaded: true };
            var diagnostics = new[] { mapRead.Error, preflightRead.Error, supervisorRead.Error }
                .Where(error => !string.IsNullOrWhiteSpace(error))
                .ToArray();
            if (!hasRemoteData && !hasLocalLayout)
            {
                throw new InvalidOperationException(
                    diagnostics.Length == 0
                        ? "MES 未返回只读就绪数据。"
                        : string.Join("；", diagnostics));
            }

            Status = diagnostics.Length == 0
                ? $"只读快照已接收：{DateTimeOffset.UtcNow:O}"
                : hasRemoteData
                    ? $"只读快照部分接收：{string.Join("；", diagnostics)}"
                    : $"MES 不可用，已加载本地地图布局：{string.Join("；", diagnostics)}";
            OfflineState.MarkReady(
                MapSnapshot is not null || Preflight is not null || hasLocalLayout,
                hasRemoteData ? "只读就绪数据已更新。" : "已加载本地地图，可离线编辑流程。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "就绪状态刷新已取消。";
            OfflineState.MarkCancelled("就绪状态刷新已取消。");
        }
        catch (Exception exception)
        {
            Status = $"就绪状态刷新失败：{exception.Message}";
            OfflineState.MarkError("就绪状态刷新失败，可点击刷新重试。", exception.Message);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public void UpdateFleet(IReadOnlyList<AgvFleetDashboardStatus> statuses) => FleetStatus = statuses ?? [];

    /// <summary>
    /// Lightweight WPF refresh used by the normal dashboard timer. The MES
    /// supervisor performs the actual device polling; this call only reads its
    /// aggregate state and never contacts a controller directly.
    /// </summary>
    public async Task RefreshSupervisorAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _mes.GetPhysicalReadinessAsync(cancellationToken);
            if (response is not null) SupervisorReadiness = response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The existing detailed preflight/error surface remains the source
            // of the visible message; a stale supervisor projection must not
            // overwrite a newer successful snapshot.
        }
    }

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
            OnPropertyChanged(nameof(LocalStationCatalog));
            OnPropertyChanged(nameof(LocalMapEdges));

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
            OnPropertyChanged(nameof(LocalStationCatalog));
            OnPropertyChanged(nameof(LocalMapEdges));
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

    private static async Task<OptionalRead<T>> ReadOptionalAsync<T>(
        Func<Task<T>> read,
        CancellationToken cancellationToken)
    {
        try
        {
            return new OptionalRead<T>(await read(), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new OptionalRead<T>(default, exception.Message);
        }
    }

    private static int ParseStationCode(string stationId, int fallback)
    {
        var digits = new string(stationId.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var code) && code > 0 ? code : fallback;
    }

    private string ResolveMappedStationId(string stationId)
    {
        if (_mapLayoutResult?.Mapping.TryResolve(stationId, out var entry) == true &&
            !string.IsNullOrWhiteSpace(entry.MesAgvStationId))
        {
            return entry.MesAgvStationId!.Trim();
        }

        return stationId.Trim();
    }

    private async Task LoadMapLayoutOnceAsync(CancellationToken cancellationToken)
    {
        if (_mapLayoutAttempted) return;
        _mapLayoutAttempted = true;
        var result = await _mapLayoutSource.LoadAsync(cancellationToken);
        _mapLayoutResult = result;
        MapLayoutError = result.Error;
        OnPropertyChanged(nameof(LocalStationCatalog));
        OnPropertyChanged(nameof(LocalMapEdges));
        if (result.Loaded && result.Layout is not null)
        {
            Map.ApplyLayout(result.Layout, result.Mapping, Map.CanvasWidth, Map.CanvasHeight);
            VerifyMapLayoutIdentity();
        }

        OnPropertyChanged(nameof(MapLayoutVerificationStatus));
        OnPropertyChanged(nameof(MapLayoutVerificationDetails));
        OnPropertyChanged(nameof(IsMapLayoutVerified));
        OnPropertyChanged(nameof(LocalStationCatalog));
        OnPropertyChanged(nameof(LocalMapEdges));
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

    private sealed record OptionalRead<T>(T? Value, string? Error);

    private sealed class EmptyMapLayoutSource : IMapLayoutSource
    {
        public static EmptyMapLayoutSource Instance { get; } = new();

        public Task<MapLayoutResult> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(new MapLayoutResult(null, MesControlAgv.Domain.Map.StationMappingConfig.Empty, Loaded: false, Error: null));
    }
}
