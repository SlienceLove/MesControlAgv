using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MesControlAgv.Contracts;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

/// <summary>配置 AGV 地图的只读几何信息和状态叠加层。</summary>
public sealed class MapViewModel : INotifyPropertyChanged
{
    private string _fingerprint = "配置地图：未知";
    private string _liveFingerprint = "实时地图：未知";
    private string _syncStatus = "未知";

    public ObservableCollection<MapNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<MapEdgeViewModel> Edges { get; } = [];
    public ObservableCollection<MapPathSegmentViewModel> AgvPathSegments { get; } = [];
    public ObservableCollection<MapAgvOverlayViewModel> Agvs { get; } = [];

    public double CanvasWidth { get; private set; } = 760;
    public double CanvasHeight { get; private set; } = 520;

    public string Fingerprint { get => _fingerprint; private set => SetField(ref _fingerprint, value); }
    public string LiveFingerprint { get => _liveFingerprint; private set => SetField(ref _liveFingerprint, value); }
    public string SyncStatus { get => _syncStatus; private set => SetField(ref _syncStatus, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(
        DashboardMapSnapshot? snapshot,
        PhysicalAgvPreflightResponse? preflight,
        IReadOnlyList<AgvFleetDashboardStatus>? fleet)
    {
        Nodes.Clear();
        Edges.Clear();
        AgvPathSegments.Clear();
        Agvs.Clear();

        if (snapshot is null)
        {
            Fingerprint = "配置地图：未知";
            LiveFingerprint = preflight?.Readiness is null
                ? "实时地图：未知"
                : FormatLive(preflight.Readiness.MapName, preflight.Readiness.MapMd5);
            SyncStatus = "未知";
            OnPropertyChanged(nameof(CanvasWidth));
            OnPropertyChanged(nameof(CanvasHeight));
            return;
        }

        Fingerprint = FormatProfile(snapshot);
        LiveFingerprint = preflight?.Readiness is { } readiness
            ? FormatLive(readiness.MapName, readiness.MapMd5)
            : "实时地图：当前驱动未提供";
        SyncStatus = CompareFingerprint(snapshot, preflight);

        var stationLookup = new Dictionary<string, MapNodeViewModel>(StringComparer.OrdinalIgnoreCase);
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

        foreach (var status in fleet ?? [])
        {
            var path = status.ActiveTask?.Path?.Where(stationLookup.ContainsKey).ToArray() ?? [];
            var current = status.Snapshot.CurrentStationId;
            var currentNode = current is not null && stationLookup.TryGetValue(current, out var selected) ? selected : null;
            Agvs.Add(new MapAgvOverlayViewModel(
                status.Snapshot.AgvId,
                current ?? "-",
                status.ActiveTask?.MesStatus ?? "空闲",
                status.ActiveTask?.DeviceState ?? "-",
                path,
                currentNode?.X ?? 8,
                currentNode?.Y ?? 8,
                status.Snapshot.Online));

            for (var pathIndex = 1; pathIndex < path.Length; pathIndex++)
            {
                var from = stationLookup[path[pathIndex - 1]];
                var to = stationLookup[path[pathIndex]];
                AgvPathSegments.Add(new MapPathSegmentViewModel(
                    status.Snapshot.AgvId,
                    from.X + 56,
                    from.Y + 30,
                    to.X + 56,
                    to.Y + 30));
            }
        }

        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
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
    double Y);

public sealed record MapEdgeViewModel(
    string From,
    string To,
    double Cost,
    bool Bidirectional,
    double X1,
    double Y1,
    double X2,
    double Y2);

public sealed record MapPathSegmentViewModel(
    string AgvId,
    double X1,
    double Y1,
    double X2,
    double Y2);

public sealed record MapAgvOverlayViewModel(
    string AgvId,
    string CurrentStation,
    string MesStatus,
    string DeviceState,
    IReadOnlyList<string> Path,
    double X,
    double Y,
    bool Online)
{
    public string PathText => Path.Count == 0 ? "暂无活动路径" : string.Join(" -> ", Path);
    public string StatusText => $"{AgvId} / {MesStatus} / {DeviceState}";
}
