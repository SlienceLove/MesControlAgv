namespace MesControlAgv.Contracts;

/// <summary>
/// Vision system capabilities description.
/// </summary>
public sealed record VisionCapabilities(
    bool SupportsColorRecognition,
    bool SupportsBarcodeReading,
    bool Supports3DLocalization,
    int MaxResolutionWidth,
    int MaxResolutionHeight);

/// <summary>
/// Vision capture command (trigger photo).
/// </summary>
public sealed record VisionCaptureCommand(
    Guid CaptureId,
    string VisionId = "VISION-01",
    string? PresetName = null,
    int? ExposureMs = null,
    int? Gain = null);

/// <summary>
/// Vision capture response.
/// </summary>
public sealed record VisionCaptureResponse(
    Guid CaptureId,
    string ImagePath,
    DateTimeOffset CapturedAt,
    string VisionId = "VISION-01");

/// <summary>
/// Vision recognition command (identify object in image).
/// </summary>
public sealed record VisionRecognitionCommand(
    Guid RecognitionId,
    string VisionId = "VISION-01",
    Guid? CaptureId = null,
    string? TargetType = null,
    string? RoiJson = null);

/// <summary>
/// Vision recognition response.
/// </summary>
public sealed record VisionRecognitionResponse(
    Guid RecognitionId,
    bool Found,
    string? RecognizedValue,
    double Confidence,
    VisionBoundingBox? BoundingBox,
    string VisionId = "VISION-01");

/// <summary>
/// Bounding box for recognized object.
/// </summary>
public sealed record VisionBoundingBox(
    int X,
    int Y,
    int Width,
    int Height);

/// <summary>
/// Vision localization command (get 3D coordinates of recognized object).
/// </summary>
public sealed record VisionLocalizationCommand(
    Guid LocalizationId,
    Guid RecognitionId,
    string VisionId = "VISION-01",
    string CoordinateFrame = "robot_base");

/// <summary>
/// Vision localization response with 3D position.
/// </summary>
public sealed record VisionLocalizationResponse(
    Guid LocalizationId,
    bool Success,
    double X,
    double Y,
    double Z,
    double? RotationDegree,
    double Confidence,
    string VisionId = "VISION-01");

/// <summary>
/// Vision calibration response (current calibration parameters).
/// </summary>
public sealed record VisionCalibrationResponse(
    bool IsCalibrated,
    string? CalibrationData,
    double? ReprojectionError,
    DateTimeOffset? LastCalibratedAt,
    string VisionId = "VISION-01");
