using MesControlAgv.Domain.Map;

namespace MesControlAgv.Wpf.Services;

public enum MapIdentityVerificationStatus
{
    Match,
    Mismatch,
    Unverifiable
}

public sealed record MapIdentityVerificationResult(
    MapIdentityVerificationStatus Status,
    IReadOnlyList<string> Reasons);

public static class MapLayoutIdentityVerifier
{
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

    public static MapIdentityVerificationResult Verify(
        SmapMapIdentity? smap,
        StationMappingConfig? mapping,
        DashboardMapSnapshot? snapshot)
    {
        if (smap is null)
        {
            return Unverifiable("已加载的 .smap 身份不可用。");
        }

        if (snapshot is null)
        {
            return Unverifiable("MES 地图快照不可用。");
        }

        mapping ??= StationMappingConfig.Empty;
        var mismatches = new List<string>();
        var unverifiable = new List<string>();

        CompareMetadata("地图名称", smap.MapName, snapshot.ProfileMapName, mismatches, unverifiable);
        CompareMetadata("地图版本", smap.MapVersion, snapshot.ProfileMapVersion, mismatches, unverifiable);
        CompareMetadata("地图 MD5", smap.Md5, snapshot.ProfileMapMd5, mismatches, unverifiable);

        var smapStations = ToIdSet(smap.StationMarks.Select(mark => Resolve(mark, mapping)));
        var mesStations = ToIdSet(snapshot.Stations.Select(station => station.AgvStationId));
        CompareSet("站点", smapStations, mesStations, mismatches, unverifiable);

        var smapEdges = ToEdgeSet(smap.DirectedEdges.Select(edge =>
            new DirectedEdge(Resolve(edge.FromMark, mapping), Resolve(edge.ToMark, mapping))));
        var mesEdges = ToEdgeSet(snapshot.Edges.SelectMany(Expand));
        CompareSet("有向边", smapEdges, mesEdges, mismatches, unverifiable);

        if (mismatches.Count > 0)
        {
            return new MapIdentityVerificationResult(
                MapIdentityVerificationStatus.Mismatch,
                mismatches.Concat(unverifiable).ToArray());
        }

        return unverifiable.Count > 0
            ? new MapIdentityVerificationResult(MapIdentityVerificationStatus.Unverifiable, unverifiable)
            : new MapIdentityVerificationResult(
                MapIdentityVerificationStatus.Match,
                [".smap 身份与 MES 地图元数据、站点和有向边一致。"]);
    }

    private static void CompareMetadata(
        string label,
        string? smapValue,
        string? profileValue,
        ICollection<string> mismatches,
        ICollection<string> unverifiable)
    {
        if (string.IsNullOrWhiteSpace(smapValue) || string.IsNullOrWhiteSpace(profileValue))
        {
            unverifiable.Add($"无法验证{label}：.smap 或 MES Profile 字段缺失。");
            return;
        }

        if (!IdComparer.Equals(smapValue.Trim(), profileValue.Trim()))
        {
            mismatches.Add($"{label}不一致：.smap='{smapValue.Trim()}'，MES Profile='{profileValue.Trim()}'。");
        }
    }

    private static void CompareSet<T>(
        string label,
        HashSet<T> smapValues,
        HashSet<T> mesValues,
        ICollection<string> mismatches,
        ICollection<string> unverifiable)
        where T : notnull
    {
        if (smapValues.Count == 0 || mesValues.Count == 0)
        {
            unverifiable.Add($"无法验证{label}集合：.smap 或 MES Profile 集合为空。");
            return;
        }

        var missingFromSmap = mesValues
            .Where(value => !smapValues.Contains(value))
            .OrderBy(value => value.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var extraInSmap = smapValues
            .Where(value => !mesValues.Contains(value))
            .OrderBy(value => value.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingFromSmap.Length == 0 && extraInSmap.Length == 0) return;

        mismatches.Add(
            $"{label}集合不一致：.smap 缺少=[{string.Join(", ", missingFromSmap)}]；" +
            $".smap 多出=[{string.Join(", ", extraInSmap)}]。");
    }

    private static string Resolve(string smapMark, StationMappingConfig mapping) =>
        mapping.TryResolve(smapMark, out var entry) && !string.IsNullOrWhiteSpace(entry.MesAgvStationId)
            ? entry.MesAgvStationId.Trim()
            : smapMark.Trim();

    private static IEnumerable<DirectedEdge> Expand(MesControlAgv.Contracts.MapEdgeResponse edge)
    {
        yield return new DirectedEdge(edge.From, edge.To);
        if (edge.Bidirectional)
        {
            yield return new DirectedEdge(edge.To, edge.From);
        }
    }

    private static HashSet<string> ToIdSet(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(IdComparer);

    private static HashSet<DirectedEdge> ToEdgeSet(IEnumerable<DirectedEdge> values) =>
        values
            .Where(edge => !string.IsNullOrWhiteSpace(edge.From) && !string.IsNullOrWhiteSpace(edge.To))
            .Select(edge => new DirectedEdge(edge.From.Trim(), edge.To.Trim()))
            .ToHashSet(DirectedEdgeComparer.Instance);

    private static MapIdentityVerificationResult Unverifiable(string reason) =>
        new(MapIdentityVerificationStatus.Unverifiable, [reason]);

    private sealed record DirectedEdge(string From, string To)
    {
        public override string ToString() => $"{From}->{To}";
    }

    private sealed class DirectedEdgeComparer : IEqualityComparer<DirectedEdge>
    {
        public static DirectedEdgeComparer Instance { get; } = new();

        public bool Equals(DirectedEdge? left, DirectedEdge? right) =>
            ReferenceEquals(left, right) ||
            left is not null && right is not null &&
            IdComparer.Equals(left.From, right.From) &&
            IdComparer.Equals(left.To, right.To);

        public int GetHashCode(DirectedEdge edge) =>
            HashCode.Combine(IdComparer.GetHashCode(edge.From), IdComparer.GetHashCode(edge.To));
    }
}
