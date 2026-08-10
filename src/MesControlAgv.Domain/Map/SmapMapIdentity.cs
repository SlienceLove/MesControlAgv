namespace MesControlAgv.Domain.Map;

public sealed record SmapDirectedEdge(string FromMark, string ToMark);

public sealed record SmapMapIdentity(
    string MapName,
    string MapVersion,
    string Md5,
    IReadOnlyList<string> StationMarks,
    IReadOnlyList<SmapDirectedEdge> DirectedEdges)
{
    public static SmapMapIdentity FromDocument(SmapDocument document, string md5)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(md5);

        return new SmapMapIdentity(
            document.Header.MapName,
            document.Header.Version,
            md5.Trim().ToLowerInvariant(),
            document.LocationMarks
                .Select(mark => mark.InstanceName)
                .Where(mark => !string.IsNullOrWhiteSpace(mark))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            document.Curves
                .Where(curve =>
                    !string.IsNullOrWhiteSpace(curve.StartMark) &&
                    !string.IsNullOrWhiteSpace(curve.EndMark))
                .Select(curve => new SmapDirectedEdge(curve.StartMark, curve.EndMark))
                .Distinct(SmapDirectedEdgeComparer.Instance)
                .ToArray());
    }

    private sealed class SmapDirectedEdgeComparer : IEqualityComparer<SmapDirectedEdge>
    {
        public static SmapDirectedEdgeComparer Instance { get; } = new();

        public bool Equals(SmapDirectedEdge? left, SmapDirectedEdge? right) =>
            ReferenceEquals(left, right) ||
            left is not null && right is not null &&
            StringComparer.OrdinalIgnoreCase.Equals(left.FromMark, right.FromMark) &&
            StringComparer.OrdinalIgnoreCase.Equals(left.ToMark, right.ToMark);

        public int GetHashCode(SmapDirectedEdge edge) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(edge.FromMark),
            StringComparer.OrdinalIgnoreCase.GetHashCode(edge.ToMark));
    }
}
