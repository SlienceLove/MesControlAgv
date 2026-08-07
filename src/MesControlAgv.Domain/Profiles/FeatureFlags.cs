namespace MesControlAgv.Domain.Profiles;

/// <summary>
/// Feature switches that change optional application behavior without changing the domain model.
/// </summary>
public sealed record FeatureFlags
{
    public bool EnableAutomaticDispatch { get; init; } = true;
    public bool EnableFieldNavigationAcceptance { get; init; }
    public bool EnableTaskCancellation { get; init; } = true;
    public bool EnableFleetMonitoring { get; init; } = true;
    public bool EnableKpi { get; init; } = true;
    public bool EnableDiagnostics { get; init; }
    public bool UseSimulator { get; init; } = true;
}
