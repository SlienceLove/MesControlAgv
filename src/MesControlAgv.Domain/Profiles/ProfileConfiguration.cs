namespace MesControlAgv.Domain.Profiles;

/// <summary>
/// Complete, transport-neutral profile configuration.
/// </summary>
public sealed record ProfileConfiguration
{
    public ProductProfile Product { get; init; } = null!;
    public IReadOnlyList<AgvProfile> Agvs { get; init; } = null!;
    public IReadOnlyList<StationProfile> Stations { get; init; } = null!;
    public MapProfile Map { get; init; } = null!;
    public PhysicalAcceptanceProfile? PhysicalAcceptance { get; init; }
    public FeatureFlags Features { get; init; } = null!;
    public TimeoutOptions Timeouts { get; init; } = null!;

    public static ProfileConfiguration Default => ProfileDefaults.CreateConfiguration();
}
