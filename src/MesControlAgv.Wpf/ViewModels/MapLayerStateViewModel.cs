using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MesControlAgv.Wpf.ViewModels;

public enum MapLayerKind
{
    Walls,
    Routes,
    StationLabels,
    RuntimeOverlays,
    RasterBackground
}

/// <summary>
/// User-controlled map visibility with a separate fail-closed runtime overlay gate.
/// </summary>
public sealed class MapLayerStateViewModel : INotifyPropertyChanged
{
    private bool _showWalls = true;
    private bool _showRoutes = true;
    private bool _showStationLabels = true;
    private bool _showRuntimeOverlays = true;
    private bool _showRasterBackground;
    private bool _runtimeOverlayAllowed = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool ShowWalls
    {
        get => _showWalls;
        set => SetField(ref _showWalls, value);
    }

    public bool ShowRoutes
    {
        get => _showRoutes;
        set => SetField(ref _showRoutes, value);
    }

    public bool ShowStationLabels
    {
        get => _showStationLabels;
        set => SetField(ref _showStationLabels, value);
    }

    public bool ShowRuntimeOverlays
    {
        get => _showRuntimeOverlays && _runtimeOverlayAllowed;
        set
        {
            var next = _runtimeOverlayAllowed && value;
            if (_showRuntimeOverlays == next) return;
            _showRuntimeOverlays = next;
            OnPropertyChanged();
        }
    }

    public bool ShowRasterBackground
    {
        get => _showRasterBackground;
        set => SetField(ref _showRasterBackground, value);
    }

    public bool RuntimeOverlayAllowed
    {
        get => _runtimeOverlayAllowed;
        private set => SetField(ref _runtimeOverlayAllowed, value);
    }

    public void SetRuntimeOverlayAllowed(bool allowed)
    {
        if (RuntimeOverlayAllowed != allowed)
        {
            RuntimeOverlayAllowed = allowed;
        }

        if (!allowed)
        {
            _showRuntimeOverlays = false;
            OnPropertyChanged(nameof(ShowRuntimeOverlays));
        }
        else
        {
            OnPropertyChanged(nameof(ShowRuntimeOverlays));
        }
    }

    public bool IsVisible(MapLayerKind layer) => layer switch
    {
        MapLayerKind.Walls => ShowWalls,
        MapLayerKind.Routes => ShowRoutes,
        MapLayerKind.StationLabels => ShowStationLabels,
        MapLayerKind.RuntimeOverlays => ShowRuntimeOverlays,
        MapLayerKind.RasterBackground => ShowRasterBackground,
        _ => false
    };

    public void SetVisible(MapLayerKind layer, bool visible)
    {
        switch (layer)
        {
            case MapLayerKind.Walls:
                ShowWalls = visible;
                break;
            case MapLayerKind.Routes:
                ShowRoutes = visible;
                break;
            case MapLayerKind.StationLabels:
                ShowStationLabels = visible;
                break;
            case MapLayerKind.RuntimeOverlays:
                ShowRuntimeOverlays = visible;
                break;
            case MapLayerKind.RasterBackground:
                ShowRasterBackground = visible;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(layer), layer, null);
        }
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
