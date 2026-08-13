namespace MesControlAgv.Contracts;

/// <summary>
/// Robot arm capabilities description.
/// </summary>
public sealed record RobotArmCapabilities(
    bool SupportsForceControl,
    bool SupportsVisionGuidance,
    bool SupportsDragTeaching,
    int MaxPayloadGrams,
    int ReachMillimeters);

/// <summary>
/// Robot arm status response.
/// </summary>
public sealed record RobotArmStatusResponse(
    string State,
    bool IsConnected,
    bool IsEnabled,
    bool HasError,
    string? ErrorMessage,
    RobotArmPose? CurrentPose,
    bool IsHoldingObject,
    int? GripperForceNewton,
    string ArmId = "ARM-01");

/// <summary>
/// Robot arm pose (position + orientation).
/// </summary>
public sealed record RobotArmPose(
    double X,
    double Y,
    double Z,
    double Rx,
    double Ry,
    double Rz);

/// <summary>
/// Pick command for robot arm.
/// </summary>
public sealed record RobotArmPickCommand(
    Guid OperationId,
    RobotArmPose TargetPose,
    string ArmId = "ARM-01",
    int? ApproachHeightMm = null,
    int? GripperForceMn = null,
    string? VisionGuidanceId = null);

/// <summary>
/// Place command for robot arm.
/// </summary>
public sealed record RobotArmPlaceCommand(
    Guid OperationId,
    RobotArmPose TargetPose,
    string ArmId = "ARM-01",
    int? ApproachHeightMm = null,
    int? ReleaseDelayMs = null);

/// <summary>
/// Move command for robot arm (without pick/place).
/// </summary>
public sealed record RobotArmMoveCommand(
    Guid OperationId,
    RobotArmPose TargetPose,
    string ArmId = "ARM-01",
    double? SpeedPercent = null);

/// <summary>
/// Robot arm operation response.
/// </summary>
public sealed record RobotArmOperationResponse(
    Guid OperationId,
    string State,
    string? ErrorMessage,
    DateTimeOffset CompletedAt,
    string ArmId = "ARM-01");
