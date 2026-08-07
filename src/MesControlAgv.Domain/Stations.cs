using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Domain;

public static class Stations
{
    private static readonly IReadOnlyDictionary<int, Station> Catalog = CreateCatalog(ProfileConfiguration.Default);

    private static IReadOnlyDictionary<int, Station> CreateCatalog(ProfileConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.Stations);
        return configuration.Stations.ToDictionary(
            station => station.Code,
            station => new Station(station.Code, station.Name, station.AgvStationId, station.Enabled));
    }

    private static readonly IReadOnlyCollection<Station> AllStations = Catalog.Values.ToArray();

    public static IReadOnlyCollection<Station> All => AllStations;

    public static IReadOnlyCollection<Station> FromProfile(ProfileConfiguration configuration) =>
        CreateCatalog(configuration).Values.ToArray();

    public static Station Get(int code) => Catalog.TryGetValue(code, out var station)
        ? station
        : throw new KeyNotFoundException($"Unknown station code: {code}.");
}
