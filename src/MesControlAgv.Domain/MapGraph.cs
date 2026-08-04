namespace MesControlAgv.Domain;

public sealed record MapEdge(string From, string To, double Cost, bool Bidirectional = true);

public sealed class AgvMap
{
    private readonly Dictionary<string, List<MapNeighbor>> _neighbors;

    public AgvMap(IEnumerable<string> stationIds, IEnumerable<MapEdge> edges)
    {
        var nodes = stationIds.ToHashSet(StringComparer.Ordinal);
        if (nodes.Count == 0) throw new ArgumentException("The map must contain at least one station.", nameof(stationIds));

        _neighbors = nodes.ToDictionary(
            stationId => stationId,
            _ => new List<MapNeighbor>(),
            StringComparer.Ordinal);

        var edgeList = edges.ToArray();
        foreach (var edge in edgeList)
        {
            if (edge.Cost <= 0) throw new ArgumentOutOfRangeException(nameof(edges), "Map edge costs must be positive.");
            if (!nodes.Contains(edge.From) || !nodes.Contains(edge.To))
            {
                throw new ArgumentException($"Map edge {edge.From} -> {edge.To} references an unknown station.", nameof(edges));
            }

            _neighbors[edge.From].Add(new MapNeighbor(edge.To, edge.Cost));
            if (edge.Bidirectional) _neighbors[edge.To].Add(new MapNeighbor(edge.From, edge.Cost));
        }

        Nodes = _neighbors.Keys.ToArray();
        Edges = edgeList;
    }

    public IReadOnlyCollection<string> Nodes { get; }
    public IReadOnlyCollection<MapEdge> Edges { get; }

    public bool Contains(string stationId) => _neighbors.ContainsKey(stationId);

    public IReadOnlyList<MapNeighbor> Neighbors(string stationId) =>
        _neighbors.TryGetValue(stationId, out var neighbors)
            ? neighbors
            : throw new KeyNotFoundException($"Station {stationId} is not present in the map.");

    public static AgvMap Default { get; } = new(
        Stations.All.Select(station => station.AgvStationId),
        [
            new("CHARGE_01", "PICK_01", 1),
            new("PICK_01", "SAMPLE_01", 1),
            new("SAMPLE_01", "ST_OPEN_01", 1),
            new("ST_OPEN_01", "ST_PREP_01", 1),
            new("ST_PREP_01", "ST_INJECT_01", 1),
            new("ST_INJECT_01", "DROP_01", 1),
            new("SAMPLE_01", "ST_PREP_01", 2)
        ]);
}

public sealed record MapNeighbor(string StationId, double Cost);

public sealed record PlannedPath(IReadOnlyList<string> Stations, double Cost)
{
    public string Start => Stations[0];
    public string End => Stations[^1];
}

public sealed class PathPlanner(AgvMap map)
{
    public PlannedPath Plan(string startStationId, string targetStationId, IReadOnlySet<string>? blockedStations = null)
    {
        if (!map.Contains(startStationId)) throw new KeyNotFoundException($"Station {startStationId} is not present in the map.");
        if (!map.Contains(targetStationId)) throw new KeyNotFoundException($"Station {targetStationId} is not present in the map.");
        if (blockedStations?.Contains(startStationId) == true || blockedStations?.Contains(targetStationId) == true)
        {
            throw new InvalidOperationException("The requested path starts or ends at a blocked station.");
        }

        if (StringComparer.Ordinal.Equals(startStationId, targetStationId))
        {
            return new PlannedPath([startStationId], 0);
        }

        var distances = map.Nodes.ToDictionary(stationId => stationId, _ => double.PositiveInfinity, StringComparer.Ordinal);
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new PriorityQueue<string, (double Cost, string Station)>();
        distances[startStationId] = 0;
        queue.Enqueue(startStationId, (0, startStationId));

        while (queue.TryDequeue(out var current, out var priority))
        {
            if (priority.Cost > distances[current]) continue;
            if (StringComparer.Ordinal.Equals(current, targetStationId)) break;

            foreach (var neighbor in map.Neighbors(current).OrderBy(item => item.StationId, StringComparer.Ordinal))
            {
                if (blockedStations?.Contains(neighbor.StationId) == true) continue;

                var candidate = distances[current] + neighbor.Cost;
                var isBetter = candidate < distances[neighbor.StationId];
                var isStableTie = candidate == distances[neighbor.StationId]
                    && (!previous.TryGetValue(neighbor.StationId, out var existingPrevious)
                        || StringComparer.Ordinal.Compare(current, existingPrevious) < 0);
                if (!isBetter && !isStableTie) continue;

                distances[neighbor.StationId] = candidate;
                previous[neighbor.StationId] = current;
                queue.Enqueue(neighbor.StationId, (candidate, neighbor.StationId));
            }
        }

        if (double.IsPositiveInfinity(distances[targetStationId]))
        {
            throw new InvalidOperationException($"No route exists from {startStationId} to {targetStationId}.");
        }

        var stations = new List<string> { targetStationId };
        while (!StringComparer.Ordinal.Equals(stations[^1], startStationId))
        {
            stations.Add(previous[stations[^1]]);
        }
        stations.Reverse();
        return new PlannedPath(stations, distances[targetStationId]);
    }

    public PlannedPath PlanVia(
        string startStationId,
        string viaStationId,
        string targetStationId,
        IReadOnlySet<string>? blockedStations = null)
    {
        var first = Plan(startStationId, viaStationId, blockedStations);
        var second = Plan(viaStationId, targetStationId, blockedStations);
        var stations = first.Stations.Concat(second.Stations.Skip(1)).ToArray();
        return new PlannedPath(stations, first.Cost + second.Cost);
    }
}
