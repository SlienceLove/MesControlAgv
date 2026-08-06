namespace MesControlAgv.Domain.Profiles;

/// <summary>
/// Identifies the product/process configuration served by the MES.
/// </summary>
public sealed record ProductProfile
{
    public string ProductId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Version { get; init; } = "1.0";
    public string? Description { get; init; }
}
