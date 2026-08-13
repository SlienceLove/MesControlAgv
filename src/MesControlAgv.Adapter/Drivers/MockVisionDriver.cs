using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Drivers;

/// <summary>
/// Mock vision system driver for development and testing without physical hardware.
/// Simulates camera capture, object recognition, and 3D localization.
/// </summary>
public sealed class MockVisionDriver : IVisionDriver
{
    public const string DriverKind = "mock-vision";

    private readonly VisionDriverOptions _options;
    private readonly Random _random = new();
    private readonly Dictionary<Guid, VisionCaptureResponse> _captures = new();
    private readonly Dictionary<Guid, VisionRecognitionResponse> _recognitions = new();
    private bool _isCalibrated = true;

    public MockVisionDriver(VisionDriverOptions? options = null)
    {
        _options = options ?? new VisionDriverOptions();
    }

    public string DriverId => DriverKind;

    public VisionCapabilities Capabilities => new(
        SupportsColorRecognition: true,
        SupportsBarcodeReading: true,
        Supports3DLocalization: true,
        MaxResolutionWidth: 1920,
        MaxResolutionHeight: 1080);

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        // Simulate connection delay
        return Task.Delay(100, cancellationToken);
    }

    public async Task<VisionCaptureResponse> CaptureAsync(
        VisionCaptureCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Simulate camera capture time
        await Task.Delay(200, cancellationToken);

        var response = new VisionCaptureResponse(
            CaptureId: command.CaptureId,
            ImagePath: $"/mock/images/capture_{command.CaptureId:N}.jpg",
            CapturedAt: DateTimeOffset.UtcNow,
            VisionId: _options.DefaultVisionId);

        _captures[command.CaptureId] = response;

        return response;
    }

    public async Task<VisionRecognitionResponse> RecognizeAsync(
        VisionRecognitionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Simulate image processing time
        await Task.Delay(300, cancellationToken);

        // Simulate occasional recognition failure (10% chance)
        var recognitionFailed = _random.Next(100) < 10;

        VisionRecognitionResponse response;

        if (recognitionFailed)
        {
            response = new VisionRecognitionResponse(
                RecognitionId: command.RecognitionId,
                Found: false,
                RecognizedValue: null,
                Confidence: 0.3,
                BoundingBox: null,
                VisionId: _options.DefaultVisionId);
        }
        else
        {
            // Generate realistic mock data
            var centerX = 960 + _random.Next(-100, 100);
            var centerY = 540 + _random.Next(-80, 80);
            var width = 120 + _random.Next(-20, 20);
            var height = 150 + _random.Next(-20, 20);

            response = new VisionRecognitionResponse(
                RecognitionId: command.RecognitionId,
                Found: true,
                RecognizedValue: command.TargetType switch
                {
                    "barcode" => $"MOCK-{_random.Next(1000, 9999)}",
                    "color" => new[] { "Red", "Blue", "Green", "Yellow" }[_random.Next(4)],
                    _ => "Object"
                },
                Confidence: 0.85 + _random.NextDouble() * 0.14, // 0.85-0.99
                BoundingBox: new VisionBoundingBox(
                    X: centerX - width / 2,
                    Y: centerY - height / 2,
                    Width: width,
                    Height: height),
                VisionId: _options.DefaultVisionId);
        }

        _recognitions[command.RecognitionId] = response;

        return response;
    }

    public async Task<VisionLocalizationResponse> LocalizeAsync(
        VisionLocalizationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Check if recognition exists
        if (!_recognitions.TryGetValue(command.RecognitionId, out var recognition))
        {
            throw new VisionDriverException(
                DriverId,
                "localize",
                $"Recognition {command.RecognitionId} not found. Call RecognizeAsync first.");
        }

        if (!recognition.Found)
        {
            return new VisionLocalizationResponse(
                LocalizationId: command.LocalizationId,
                Success: false,
                X: 0,
                Y: 0,
                Z: 0,
                RotationDegree: null,
                Confidence: 0,
                VisionId: _options.DefaultVisionId);
        }

        // Simulate 3D localization computation
        await Task.Delay(150, cancellationToken);

        // Generate realistic 3D coordinates in robot base frame
        // Assuming object is within robot's reach (300-600mm from base)
        var x = 300 + _random.Next(-50, 50); // mm
        var y = 150 + _random.Next(-100, 100);
        var z = 5 + _random.Next(-2, 3); // Height on table surface
        var rotation = _random.Next(-30, 30); // degrees

        var response = new VisionLocalizationResponse(
            LocalizationId: command.LocalizationId,
            Success: true,
            X: x,
            Y: y,
            Z: z,
            RotationDegree: rotation,
            Confidence: recognition.Confidence * 0.95, // Slightly lower than recognition
            VisionId: _options.DefaultVisionId);

        return response;
    }

    public Task<VisionCalibrationResponse> GetCalibrationAsync(CancellationToken cancellationToken)
    {
        var response = new VisionCalibrationResponse(
            IsCalibrated: _isCalibrated,
            CalibrationData: _isCalibrated
                ? "{\"fx\":800,\"fy\":800,\"cx\":960,\"cy\":540,\"distortion\":[0.1,-0.05,0,0,0]}"
                : null,
            ReprojectionError: _isCalibrated ? 0.3 : null,
            LastCalibratedAt: _isCalibrated ? DateTimeOffset.UtcNow.AddDays(-7) : null,
            VisionId: _options.DefaultVisionId);

        return Task.FromResult(response);
    }
}

public sealed class MockVisionDriverFactory : IVisionDriverFactory
{
    public string DriverId => MockVisionDriver.DriverKind;

    public IVisionDriver Create(VisionDriverOptions options) =>
        new MockVisionDriver(options);
}
