using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Builds a deterministic, read-only description of the profile and routing
/// graph currently served by MES. These fingerprints describe MES
/// configuration; they are not controller map checksums or physical release
/// evidence.
/// </summary>
public sealed class RuntimeReadinessService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly ProfileConfiguration _profile;
    private readonly AgvMap _map;

    public RuntimeReadinessService(ProfileConfiguration profile, AgvMap map)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _map = map ?? throw new ArgumentNullException(nameof(map));
    }

    public RuntimeReadinessResponse Get()
    {
        var stationIds = _map.Nodes
            .OrderBy(stationId => stationId, StringComparer.Ordinal)
            .ToArray();
        var directedEdges = BuildDirectedEdges(_map);

        return new RuntimeReadinessResponse(
            _profile.Product.ProductId,
            _profile.Product.DisplayName,
            _profile.Product.Version,
            _profile.Features.UseSimulator,
            _profile.Features.EnableAutomaticDispatch,
            _profile.Features.EnableTaskCancellation,
            CreateFingerprint(CreateProfileCanonicalModel(directedEdges)),
            CreateFingerprint(CreateMapCanonicalModel(stationIds, directedEdges)),
            stationIds,
            directedEdges,
            _profile.PhysicalAcceptance?.MapSnapshot?.MapName,
            _profile.PhysicalAcceptance?.MapSnapshot?.Version,
            _profile.PhysicalAcceptance?.MapSnapshot?.Md5);
    }

    private object CreateProfileCanonicalModel(IReadOnlyList<DirectedMapEdgeResponse> directedEdges) => new
    {
        Product = new
        {
            _profile.Product.ProductId,
            _profile.Product.DisplayName,
            _profile.Product.Version,
            _profile.Product.Description
        },
        Features = new
        {
            _profile.Features.UseSimulator,
            _profile.Features.EnableAutomaticDispatch,
            _profile.Features.EnableTaskCancellation,
            _profile.Features.EnableFleetMonitoring,
            _profile.Features.EnableKpi,
            _profile.Features.EnableDiagnostics,
            _profile.Features.EnableFieldNavigationAcceptance
        },
        Timeouts = new
        {
            ConnectionTimeoutTicks = _profile.Timeouts.ConnectionTimeout.Ticks,
            DispatchTimeoutTicks = _profile.Timeouts.DispatchTimeout.Ticks,
            CommandTimeoutTicks = _profile.Timeouts.CommandTimeout.Ticks,
            TaskCompletionTimeoutTicks = _profile.Timeouts.TaskCompletionTimeout.Ticks,
            TaskPollingIntervalTicks = _profile.Timeouts.TaskPollingInterval.Ticks
        },
        Agvs = (_profile.Agvs ?? [])
            .OrderBy(agv => agv.AgvId, StringComparer.Ordinal)
            .Select(agv => new
            {
                agv.AgvId,
                agv.Model,
                agv.Driver,
                agv.Endpoint,
                DeviceParameters = (agv.DeviceParameters ?? new Dictionary<string, string>())
                    .OrderBy(parameter => parameter.Key, StringComparer.Ordinal)
                    .Select(parameter => new { parameter.Key, parameter.Value })
                    .ToArray(),
                agv.MaxLoadKg,
                agv.MaxSpeedMetersPerSecond,
                agv.HomeStationId,
                agv.Enabled
            })
            .ToArray(),
        Stations = (_profile.Stations ?? [])
            .OrderBy(station => station.Code)
            .ThenBy(station => station.AgvStationId, StringComparer.Ordinal)
            .Select(station => new
            {
                station.Code,
                station.StationId,
                station.AgvStationId,
                station.Name,
                station.Type,
                station.Capacity,
                station.Enabled
            })
            .ToArray(),
        Map = CreateMapCanonicalModel(
            _map.Nodes.OrderBy(stationId => stationId, StringComparer.Ordinal).ToArray(),
            directedEdges),
        PhysicalAcceptance = CreatePhysicalCanonicalModel(_profile.PhysicalAcceptance)
    };

    private static object CreateMapCanonicalModel(
        IReadOnlyList<string> stationIds,
        IReadOnlyList<DirectedMapEdgeResponse> directedEdges) => new
        {
            StationIds = stationIds,
            DirectedEdges = directedEdges
                .OrderBy(edge => edge.From, StringComparer.Ordinal)
                .ThenBy(edge => edge.To, StringComparer.Ordinal)
                .ThenBy(edge => edge.Cost)
                .Select(edge => new
                {
                    edge.From,
                    edge.To,
                    Cost = edge.Cost.ToString("R", CultureInfo.InvariantCulture)
                })
                .ToArray()
        };

    private static object? CreatePhysicalCanonicalModel(PhysicalAcceptanceProfile? physical)
    {
        if (physical is null) return null;

        var snapshot = physical.MapSnapshot;
        return new
        {
            physical.ExpectedControlOwner,
            MapSnapshot = snapshot is null
                ? null
                : new
                {
                    snapshot.MapName,
                    snapshot.Version,
                    snapshot.Md5,
                    CapturedAtUtc = snapshot.CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    StationIds = (snapshot.StationIds ?? [])
                        .OrderBy(stationId => stationId, StringComparer.Ordinal)
                        .ToArray(),
                    DirectedEdges = (snapshot.DirectedEdges ?? [])
                        .OrderBy(edge => edge.From, StringComparer.Ordinal)
                        .ThenBy(edge => edge.To, StringComparer.Ordinal)
                        .Select(edge => new { edge.From, edge.To })
                        .ToArray()
                },
            Safety = physical.Safety is null
                ? null
                : new
                {
                    physical.Safety.MinimumLocalizationConfidence,
                    physical.Safety.MaximumDispatchSpeedMetersPerSecond,
                    physical.Safety.RequireControlOwnership,
                    physical.Safety.RequireNoEmergency,
                    physical.Safety.RequireNoBlocked,
                    physical.Safety.RequireNoFaults,
                    physical.Safety.RequireAutomaticMode
                }
        };
    }

    private static IReadOnlyList<DirectedMapEdgeResponse> BuildDirectedEdges(AgvMap map)
    {
        var edges = new List<DirectedMapEdgeResponse>();
        foreach (var edge in map.Edges)
        {
            edges.Add(new DirectedMapEdgeResponse(edge.From, edge.To, edge.Cost));
            if (edge.Bidirectional)
            {
                edges.Add(new DirectedMapEdgeResponse(edge.To, edge.From, edge.Cost));
            }
        }

        return edges
            .OrderBy(edge => edge.From, StringComparer.Ordinal)
            .ThenBy(edge => edge.To, StringComparer.Ordinal)
            .ThenBy(edge => edge.Cost)
            .ToArray();
    }

    private static string CreateFingerprint(object canonicalModel)
    {
        var json = JsonSerializer.Serialize(canonicalModel, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
