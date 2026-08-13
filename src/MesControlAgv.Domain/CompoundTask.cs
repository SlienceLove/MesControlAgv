using MesControlAgv.Contracts;

namespace MesControlAgv.Domain;

/// <summary>
/// Compound transport task that coordinates AGV navigation, vision recognition, and robot arm operations.
/// </summary>
public sealed record CompoundTransportTask(
    Guid TaskId,
    string SourceStationId,
    string TargetStationId,
    CompoundTaskProfile Profile);

/// <summary>
/// Configuration profile for compound task execution.
/// </summary>
public sealed record CompoundTaskProfile(
    bool RequireVisionGuidance,
    bool RequirePickAtSource,
    bool RequirePlaceAtTarget,
    string? VisionPresetName = null,
    RobotArmPose? DefaultPickPose = null,
    RobotArmPose? DefaultPlacePose = null,
    int MaxVisionRetries = 2,
    double MinVisionConfidence = 0.80);

/// <summary>
/// State machine for compound task execution.
/// </summary>
public enum CompoundTaskState
{
    Created,
    NavigatingToSource,
    ArrivedAtSource,
    VisionScanning,
    VisionCompleted,
    VisionFailed,
    Picking,
    PickCompleted,
    PickFailed,
    NavigatingToTarget,
    ArrivedAtTarget,
    Placing,
    PlaceCompleted,
    PlaceFailed,
    ReturningHome,
    Completed,
    Failed
}

/// <summary>
/// Result of compound task execution.
/// </summary>
public sealed record CompoundTaskResult(
    Guid TaskId,
    CompoundTaskState FinalState,
    bool Success,
    string? ErrorMessage,
    CompoundTaskExecutionDetails? Details = null);

/// <summary>
/// Detailed execution information for compound task.
/// </summary>
public sealed record CompoundTaskExecutionDetails(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    TimeSpan TotalDuration,
    Guid? AgvNavigationTaskId = null,
    Guid? VisionCaptureId = null,
    Guid? VisionRecognitionId = null,
    Guid? VisionLocalizationId = null,
    RobotArmPose? ActualPickPose = null,
    Guid? PickOperationId = null,
    Guid? PlaceOperationId = null,
    int VisionRetryCount = 0);
