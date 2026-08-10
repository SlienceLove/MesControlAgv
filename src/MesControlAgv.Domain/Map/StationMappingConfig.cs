namespace MesControlAgv.Domain.Map;

public sealed record StationMappingEntry(string SmapMark, string? MesAgvStationId, string? DisplayName);

public sealed record StationMappingConfig(IReadOnlyList<StationMappingEntry> Entries)
{
    public static StationMappingConfig Empty { get; } = new(Array.Empty<StationMappingEntry>());

    public bool TryResolve(string smapMark, out StationMappingEntry entry)
    {
        entry = Entries.FirstOrDefault(candidate =>
            candidate.SmapMark.Equals(smapMark, StringComparison.OrdinalIgnoreCase))!;
        return entry is not null;
    }
}
