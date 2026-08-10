using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using MesControlAgv.Domain.Map;
using MesControlAgv.Wpf.Services;

namespace MesControlAgv.Wpf.ViewModels;

public sealed record MapStationDetail(
    string SmapStationId,
    string? MesStationId,
    string DisplayName,
    MapPoint Physical,
    bool Enabled,
    IReadOnlyList<string> RelatedRouteIds)
{
    public string CoordinateText =>
        $"({Physical.X.ToString("0.###", CultureInfo.InvariantCulture)}, {Physical.Y.ToString("0.###", CultureInfo.InvariantCulture)})";

    public string RouteText => RelatedRouteIds.Count == 0
        ? "None"
        : string.Join(", ", RelatedRouteIds);

    public string MesStationText => string.IsNullOrWhiteSpace(MesStationId) ? "-" : MesStationId;
}

/// <summary>Read-only station selection and details for the map inspector.</summary>
public sealed class MapStationSelectionViewModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, MapStationDetail> _details = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedStationId;

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<MapStationDetail> Stations => _details.Values.ToArray();

    public string? SelectedStationId
    {
        get => _selectedStationId;
        private set => SetField(ref _selectedStationId, value);
    }

    public MapStationDetail? SelectedDetail =>
        SelectedStationId is not null && _details.TryGetValue(SelectedStationId, out var detail)
            ? detail
            : null;

    public bool HasSelection => SelectedDetail is not null;

    public void ApplyLayout(MapLayout layout, StationMappingConfig mapping)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(mapping);

        _details.Clear();
        foreach (var station in layout.Stations)
        {
            var mapped = mapping.TryResolve(station.Id, out var entry) ? entry : null;
            var routeIds = layout.Routes
                .Where(route =>
                    route.FromStationId.Equals(station.Id, StringComparison.OrdinalIgnoreCase) ||
                    route.ToStationId.Equals(station.Id, StringComparison.OrdinalIgnoreCase))
                .Select(route => route.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _details[station.Id] = new MapStationDetail(
                station.Id,
                mapped?.MesAgvStationId,
                string.IsNullOrWhiteSpace(mapped?.DisplayName) ? station.Id : mapped.DisplayName!,
                station.Physical,
                Enabled: true,
                routeIds);
        }

        if (SelectedStationId is not null && !_details.ContainsKey(SelectedStationId))
        {
            Clear();
        }
        else
        {
            OnPropertyChanged(nameof(Stations));
            OnPropertyChanged(nameof(SelectedDetail));
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public void ClearLayout()
    {
        _details.Clear();
        Clear();
        OnPropertyChanged(nameof(Stations));
    }

    public bool Select(string? stationId)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        {
            Clear();
            return false;
        }

        var smapId = ResolveSmapId(stationId);
        if (smapId is null) return false;
        SelectedStationId = smapId;
        OnPropertyChanged(nameof(SelectedDetail));
        OnPropertyChanged(nameof(HasSelection));
        return true;
    }

    public void Clear()
    {
        if (SelectedStationId is null) return;
        SelectedStationId = null;
        OnPropertyChanged(nameof(SelectedDetail));
        OnPropertyChanged(nameof(HasSelection));
    }

    public bool UpdateStatus(string stationId, bool enabled, string? displayName = null)
    {
        var smapId = ResolveSmapId(stationId);
        if (smapId is null || !_details.TryGetValue(smapId, out var detail)) return false;
        var next = detail with
        {
            Enabled = enabled,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? detail.DisplayName : displayName
        };
        if (next == detail) return false;
        _details[smapId] = next;
        OnPropertyChanged(nameof(Stations));
        OnPropertyChanged(nameof(SelectedDetail));
        return true;
    }

    private string? ResolveSmapId(string stationId)
    {
        if (_details.ContainsKey(stationId)) return stationId;
        return _details.Values.FirstOrDefault(detail =>
            detail.MesStationId is not null &&
            detail.MesStationId.Equals(stationId, StringComparison.OrdinalIgnoreCase))?.SmapStationId;
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
