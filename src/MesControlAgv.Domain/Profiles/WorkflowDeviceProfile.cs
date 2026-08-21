namespace MesControlAgv.Domain.Profiles;

/// <summary>
/// Transport-neutral device availability used while validating workflow definitions.
/// It intentionally contains no endpoint, protocol, register, or command metadata.
/// </summary>
public sealed record WorkflowDeviceProfile
{
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceFamily { get; init; } = string.Empty;
    public IReadOnlyList<string> CapabilityIds { get; init; } = Array.Empty<string>();
    public bool Enabled { get; init; } = true;
    public bool ControlEnabled { get; init; }
}
