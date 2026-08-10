using MesControlAgv.Domain.Map;

namespace MesControlAgv.Wpf.Services;

public interface IMapLayoutSource
{
    Task<MapLayoutResult> LoadAsync(CancellationToken ct = default);
}

public sealed record MapLayoutResult(
    MapLayout? Layout,
    StationMappingConfig Mapping,
    bool Loaded,
    string? Error,
    SmapMapIdentity? Identity = null);
