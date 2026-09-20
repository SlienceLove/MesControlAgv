namespace MesControlAgv.Contracts;

/// <summary>Read-only map pose. Units: metres and radians; Angle is map heading, not IMU yaw.</summary>
public sealed record AgvPoseResponse(
    string AgvId, double? X, double? Y, double? Angle, double? Confidence,
    string? CurrentStation, string? ControllerTimestamp, DateTimeOffset ReceivedAt,
    string? MapName, string? MapMd5, DateTimeOffset? MapObservedAt);
