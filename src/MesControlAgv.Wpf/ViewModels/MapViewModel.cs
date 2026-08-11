using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MesControlAgv.Contracts;
using MesControlAgv.Domain.Map;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>配置 AGV 地图的只读几何信息和状态叠加层。</summary>
public sealed class MapViewModel : INotifyPropertyChanged
{
    public const double DefaultCanvasWidth = 1140;
    public const double DefaultCanvasHeight = 780;

    private const double NodeWidth = 112;
    private const double NodeHeight = 60;
    private const double NodeCenterX = NodeWidth / 2;
    private const double NodeCenterY = NodeHeight / 2;
    private const double SmapPadding = NodeCenterX;

    private string _fingerprint = "配置地图：未知";
    private string _liveFingerprint = "实时地图：未知";
    private string _syncStatus = "未知";
    private bool _usingSmapLayout;
    private bool _smapOverlayAllowed = true;
    private readonly Dictionary<string, string> _smapStationAliases = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<MapNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<MapEdgeViewModel> Edges { get; } = [];
    public ObservableCollection<MapPathSegmentViewModel> WallSegments { get; } = [];
    public ObservableCollection<MapPathSegmentViewModel> AgvPathSegments { get; } = [];
    public ObservableCollection<MapAgvOverlayViewModel> Agvs { get; } = [];
    public ObservableCollection<MapAgvOverlayViewModel> VisualAgvs { get; } = [];
    public MapLayerStateViewModel Layers { get; } = new();
    public MapRasterLayerViewModel Raster { get; } = new();
    public MapStationSelectionViewModel StationSelection { get; } = new();

    public double CanvasWidth { get; private set; } = DefaultCanvasWidth;
    public double CanvasHeight { get; private set; } = DefaultCanvasHeight;
    public MapViewportBounds NavigationBounds { get; private set; } = new(0, 0, DefaultCanvasWidth, DefaultCanvasHeight);

    public string Fingerprint { get => _fingerprint; private set => SetField(ref _fingerprint, value); }
    public string LiveFingerprint { get => _liveFingerprint; private set => SetField(ref _liveFingerprint, value); }
    public string SyncStatus { get => _syncStatus; private set => SetField(ref _syncStatus, value); }
    public bool UsingSmapLayout { get => _usingSmapLayout; private set => SetField(ref _usingSmapLayout, value); }
    public bool SmapOverlayAllowed
    {
        get => _smapOverlayAllowed;
        private set => SetField(ref _smapOverlayAllowed, value);
    }

    public MapViewModel()
    {
        Layers.PropertyChanged += Layers_PropertyChanged;
        Raster.SetVisible(Layers.ShowRasterBackground);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyLayout(
        MapLayout layout,
        StationMappingConfig mapping,
        double canvasWidth,
        double canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(mapping);

        Nodes.Clear();
        Edges.Clear();
        WallSegments.Clear();
        AgvPathSegments.Clear();
        Agvs.Clear();
        _smapStationAliases.Clear();
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        Raster.ApplyLayout(layout, canvasWidth, canvasHeight, SmapPadding);
        Raster.SetVisible(Layers.ShowRasterBackground);
        StationSelection.ApplyLayout(layout, mapping);

        var coordinates = new MapCoordinateSystem(layout.Header, canvasWidth, canvasHeight, SmapPadding);
        var stations = new Dictionary<string, MapNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var station in layout.Stations)
        {
            var center = coordinates.ToCanvas(station.Physical);
            var name = mapping.TryResolve(station.Id, out var entry) &&
                !string.IsNullOrWhiteSpace(entry.DisplayName)
                    ? entry.DisplayName!
                    : station.Id;
            var node = new MapNodeViewModel(
                station.Id,
                name,
                0,
                true,
                center.X - NodeCenterX,
                center.Y - NodeCenterY);
            Nodes.Add(node);
            stations[station.Id] = node;
            _smapStationAliases[station.Id] = station.Id;
            if (mapping.TryResolve(station.Id, out entry) &&
                !string.IsNullOrWhiteSpace(entry.MesAgvStationId))
            {
                _smapStationAliases[entry.MesAgvStationId!] = station.Id;
            }
        }

        foreach (var route in layout.Routes)
        {
            if (!stations.ContainsKey(route.FromStationId) || !stations.ContainsKey(route.ToStationId)) continue;
            // The smap direction field is undocumented at this site. Direction comes from
            // advancedCurve start/end; only a separate reverse curve proves bidirectionality.
            var hasReverseRoute = !route.FromStationId.Equals(route.ToStationId, StringComparison.OrdinalIgnoreCase) &&
                layout.Routes.Any(candidate =>
                    !ReferenceEquals(candidate, route) &&
                    candidate.FromStationId.Equals(route.ToStationId, StringComparison.OrdinalIgnoreCase) &&
                    candidate.ToStationId.Equals(route.FromStationId, StringComparison.OrdinalIgnoreCase));
            var from = coordinates.ToCanvas(route.From);
            var to = coordinates.ToCanvas(route.To);
            var control1 = coordinates.ToCanvas(route.Control1);
            var control2 = coordinates.ToCanvas(route.Control2);
            Edges.Add(new MapEdgeViewModel(
                route.FromStationId,
                route.ToStationId,
                0,
                hasReverseRoute,
                from.X,
                from.Y,
                to.X,
                to.Y,
                control1.X,
                control1.Y,
                control2.X,
                control2.Y));
        }

        foreach (var wall in layout.Walls.Lines)
        {
            var start = coordinates.ToCanvas(wall.Start);
            var end = coordinates.ToCanvas(wall.End);
            WallSegments.Add(new MapPathSegmentViewModel(string.Empty, start.X, start.Y, end.X, end.Y));
        }

        UpdateNavigationBounds();
        UsingSmapLayout = true;
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(Raster));
    }

    /// <summary>
    /// Allows the readiness layer to fail closed when the loaded geometry cannot
    /// be proven to match the MES profile. The textual fleet list remains useful,
    /// while unverified positions and paths stay off the map canvas.
    /// </summary>
    public void SetSmapOverlayAllowed(bool allowed)
    {
        SmapOverlayAllowed = allowed;
        Layers.SetRuntimeOverlayAllowed(allowed);
        if (UsingSmapLayout && !allowed)
        {
            VisualAgvs.Clear();
            AgvPathSegments.Clear();
        }
    }

    public void Update(
        DashboardMapSnapshot? snapshot,
        PhysicalAgvPreflightResponse? preflight,
        IReadOnlyList<AgvFleetDashboardStatus>? fleet)
    {
        if (!UsingSmapLayout)
        {
            SmapOverlayAllowed = true;
            Layers.SetRuntimeOverlayAllowed(true);
            Nodes.Clear();
            Edges.Clear();
            WallSegments.Clear();
            Raster.ApplyLayout(null, CanvasWidth, CanvasHeight);
            StationSelection.ClearLayout();
            OnPropertyChanged(nameof(Raster));
        }
        AgvPathSegments.Clear();
        Agvs.Clear();
        VisualAgvs.Clear();

        if (snapshot is null)
        {
            if (!UsingSmapLayout) UpdateNavigationBounds();
            Fingerprint = "配置地图：未知";
            LiveFingerprint = preflight?.Readiness is null
                ? "实时地图：未知"
                : FormatLive(preflight.Readiness.MapName, preflight.Readiness.MapMd5);
            SyncStatus = "未知";
            OnPropertyChanged(nameof(CanvasWidth));
            OnPropertyChanged(nameof(CanvasHeight));
            if (UsingSmapLayout) UpdateFleetOverlays(fleet);
            return;
        }

        Fingerprint = FormatProfile(snapshot);
        LiveFingerprint = preflight?.Readiness is { } readiness
            ? FormatLive(readiness.MapName, readiness.MapMd5)
            : "实时地图：当前驱动未提供";
        SyncStatus = CompareFingerprint(snapshot, preflight);
        foreach (var station in snapshot.Stations)
        {
            StationSelection.UpdateStatus(station.AgvStationId, station.Enabled);
        }

        var stationLookup = Nodes.ToDictionary(node => node.StationId, StringComparer.OrdinalIgnoreCase);
        if (UsingSmapLayout)
        {
            foreach (var alias in _smapStationAliases)
            {
                if (stationLookup.TryGetValue(alias.Value, out var node))
                {
                    stationLookup[alias.Key] = node;
                }
            }
        }
        if (!UsingSmapLayout)
        {
            stationLookup = new Dictionary<string, MapNodeViewModel>(StringComparer.OrdinalIgnoreCase);
            var columns = Math.Clamp((int)Math.Ceiling(Math.Sqrt(snapshot.Stations.Count)), 2, 5);
            var index = 0;
            foreach (var station in snapshot.Stations)
            {
                var node = new MapNodeViewModel(
                    station.AgvStationId,
                    station.Name,
                    station.Code,
                    station.Enabled,
                    56 + (index % columns) * 156,
                    54 + (index / columns) * 112);
                Nodes.Add(node);
                stationLookup[node.StationId] = node;
                index++;
            }

            CanvasWidth = Math.Max(760, 112 + columns * 156);
            CanvasHeight = Math.Max(520, 116 + (int)Math.Ceiling(Math.Max(snapshot.Stations.Count, 1) / (double)columns) * 112);
            foreach (var edge in snapshot.Edges)
            {
                if (!stationLookup.TryGetValue(edge.From, out var from) ||
                    !stationLookup.TryGetValue(edge.To, out var to))
                {
                    continue;
                }

                Edges.Add(new MapEdgeViewModel(edge.From, edge.To, edge.Cost, edge.Bidirectional, from.X + 56, from.Y + 30, to.X + 56, to.Y + 30));
            }

            UpdateNavigationBounds();
        }

        UpdateFleetOverlays(fleet, stationLookup);
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
    }

    public bool SelectStation(string? stationId) => StationSelection.Select(stationId);

    private void Layers_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MapLayerStateViewModel.ShowRasterBackground)) return;
        Raster.SetVisible(Layers.ShowRasterBackground);
    }

    private void UpdateNavigationBounds()
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;

        void Include(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) return;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        foreach (var node in Nodes)
        {
            Include(node.X, node.Y);
            Include(node.X + NodeWidth, node.Y + NodeHeight);
        }

        foreach (var edge in Edges)
        {
            Include(edge.X1, edge.Y1);
            Include(edge.X2, edge.Y2);
            Include(edge.Control1X, edge.Control1Y);
            Include(edge.Control2X, edge.Control2Y);
        }

        var nextBounds = double.IsFinite(minX) && double.IsFinite(minY) &&
            double.IsFinite(maxX) && double.IsFinite(maxY) && maxX > minX && maxY > minY
                ? new MapViewportBounds(minX, minY, maxX - minX, maxY - minY)
                : new MapViewportBounds(0, 0, CanvasWidth, CanvasHeight);
        if (NavigationBounds == nextBounds) return;
        NavigationBounds = nextBounds;
        OnPropertyChanged(nameof(NavigationBounds));
    }

    private void UpdateFleetOverlays(
        IReadOnlyList<AgvFleetDashboardStatus>? fleet,
        IReadOnlyDictionary<string, MapNodeViewModel>? lookup = null)
    {
        var stationLookup = lookup is null
            ? Nodes.ToDictionary(node => node.StationId, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, MapNodeViewModel>(lookup, StringComparer.OrdinalIgnoreCase);
        if (UsingSmapLayout)
        {
            foreach (var alias in _smapStationAliases)
            {
                if (stationLookup.TryGetValue(alias.Value, out var node))
                {
                    stationLookup[alias.Key] = node;
                }
            }
        }
        var renderGeometry = !UsingSmapLayout || SmapOverlayAllowed;
        foreach (var status in fleet ?? [])
        {
            var path = status.ActiveTask?.Path?.Where(stationLookup.ContainsKey).ToArray() ?? [];
            var current = status.Snapshot.CurrentStationId;
            var currentNode = current is not null && stationLookup.TryGetValue(current, out var selected) ? selected : null;
            var currentX = currentNode is null
                ? 8
                : UsingSmapLayout ? currentNode.X + NodeCenterX : currentNode.X;
            var currentY = currentNode is null
                ? 8
                : UsingSmapLayout ? currentNode.Y + NodeCenterY : currentNode.Y;
            var overlay = new MapAgvOverlayViewModel(
                status.Snapshot.AgvId,
                current ?? "-",
                status.ActiveTask?.MesStatus ?? "空闲",
                status.ActiveTask?.DeviceState ?? "-",
                path,
                currentX,
                currentY,
                status.Snapshot.Online);
            Agvs.Add(overlay);
            if (renderGeometry)
            {
                VisualAgvs.Add(overlay);
            }

            if (!renderGeometry) continue;

            for (var pathIndex = 1; pathIndex < path.Length; pathIndex++)
            {
                var from = stationLookup[path[pathIndex - 1]];
                var to = stationLookup[path[pathIndex]];
                var route = UsingSmapLayout
                    ? Edges.FirstOrDefault(edge =>
                        edge.From.Equals(from.StationId, StringComparison.OrdinalIgnoreCase) &&
                        edge.To.Equals(to.StationId, StringComparison.OrdinalIgnoreCase))
                    : null;
                AgvPathSegments.Add(route is null
                    ? new MapPathSegmentViewModel(
                        status.Snapshot.AgvId,
                        from.X + NodeCenterX,
                        from.Y + NodeCenterY,
                        to.X + NodeCenterX,
                        to.Y + NodeCenterY)
                    : new MapPathSegmentViewModel(
                        status.Snapshot.AgvId,
                        route.X1,
                        route.Y1,
                        route.X2,
                        route.Y2,
                        route.Control1X,
                        route.Control1Y,
                        route.Control2X,
                        route.Control2Y));
            }
        }
    }

    private static string FormatProfile(DashboardMapSnapshot snapshot) =>
        $"配置地图：{snapshot.ProfileMapName ?? "未知"} / {snapshot.ProfileMapVersion ?? "未知"} / {snapshot.ProfileMapMd5 ?? "未知"}";

    private static string FormatLive(string? name, string? md5) =>
        $"实时地图：{name ?? "未知"} / {md5 ?? "未知"}";

    private static string CompareFingerprint(DashboardMapSnapshot snapshot, PhysicalAgvPreflightResponse? preflight)
    {
        var live = preflight?.Readiness;
        if (live is null || string.IsNullOrWhiteSpace(snapshot.ProfileMapMd5) || string.IsNullOrWhiteSpace(live.MapMd5)) return "未知";
        return StringComparer.OrdinalIgnoreCase.Equals(snapshot.ProfileMapMd5, live.MapMd5) &&
            (string.IsNullOrWhiteSpace(snapshot.ProfileMapName) || string.IsNullOrWhiteSpace(live.MapName) ||
             StringComparer.OrdinalIgnoreCase.Equals(snapshot.ProfileMapName, live.MapName))
            ? "一致"
            : "不一致";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record MapNodeViewModel(
    string StationId,
    string Name,
    int Code,
    bool Enabled,
    double X,
    double Y)
{
    public string DisplayText => string.Equals(StationId, Name, StringComparison.OrdinalIgnoreCase)
        ? StationId
        : $"{StationId} / {Name}";
}

public sealed record MapEdgeViewModel(
    string From,
    string To,
    double Cost,
    bool Bidirectional,
    double X1,
    double Y1,
    double X2,
    double Y2,
    double Control1X = double.NaN,
    double Control1Y = double.NaN,
    double Control2X = double.NaN,
    double Control2Y = double.NaN)
{
    private const double ArrowProgress = 0.62;

    public double ArrowX => CoordinateAt(X1, Control1X, Control2X, X2, ArrowProgress);
    public double ArrowY => CoordinateAt(Y1, Control1Y, Control2Y, Y2, ArrowProgress);
    public double ArrowAngle
    {
        get
        {
            if (!HasBezierControls)
            {
                return Math.Atan2(Y2 - Y1, X2 - X1) * 180 / Math.PI;
            }

            var t = ArrowProgress;
            var u = 1 - t;
            var dx =
                (3 * u * u * (Control1X - X1)) +
                (6 * u * t * (Control2X - Control1X)) +
                (3 * t * t * (X2 - Control2X));
            var dy =
                (3 * u * u * (Control1Y - Y1)) +
                (6 * u * t * (Control2Y - Control1Y)) +
                (3 * t * t * (Y2 - Control2Y));
            return Math.Atan2(dy, dx) * 180 / Math.PI;
        }
    }

    private bool HasBezierControls =>
        double.IsFinite(Control1X) &&
        double.IsFinite(Control1Y) &&
        double.IsFinite(Control2X) &&
        double.IsFinite(Control2Y);

    private double CoordinateAt(double start, double control1, double control2, double end, double progress)
    {
        if (!HasBezierControls) return start + ((end - start) * progress);

        var u = 1 - progress;
        return
            (u * u * u * start) +
            (3 * u * u * progress * control1) +
            (3 * u * progress * progress * control2) +
            (progress * progress * progress * end);
    }
}

public sealed record MapPathSegmentViewModel(
    string AgvId,
    double X1,
    double Y1,
    double X2,
    double Y2,
    double Control1X = double.NaN,
    double Control1Y = double.NaN,
    double Control2X = double.NaN,
    double Control2Y = double.NaN);

public sealed record MapAgvOverlayViewModel(
    string AgvId,
    string CurrentStation,
    string MesStatus,
    string DeviceState,
    IReadOnlyList<string> Path,
    double X,
    double Y,
    bool Online,
    double HeadingRadians = 0,
    bool IsAnimating = false)
{
    public string PathText => Path.Count == 0 ? "暂无活动路径" : string.Join(" -> ", Path);
    public string StatusText => $"{AgvId} / {MesStatus} / {DeviceState}";
    public double HeadingDegrees => HeadingRadians * 180 / Math.PI;
}
