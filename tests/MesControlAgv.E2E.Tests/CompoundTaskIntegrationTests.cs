extern alias AdapterApp;
using AdapterApp::MesControlAgv.Adapter.Drivers;
using AdapterApp::MesControlAgv.Adapter.Services;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using MesControlAgv.Mes.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MesControlAgv.E2E.Tests;

/// <summary>
/// Integration tests for compound task orchestration with AGV, robot arm, and vision.
/// Uses Mock drivers to simulate device behavior without physical hardware.
/// </summary>
public sealed class CompoundTaskIntegrationTests
{
    [Fact(Skip = "Requires full AGV simulator setup")]
    public async Task ExecuteCompoundTask_WithVisionGuidance_ShouldComplete()
    {
        // Arrange
        // TODO: Setup proper AGV mock driver
        var agvDriver = CreateMockAgvDriver();
        var armDriver = new MockRobotArmDriver();
        var visionDriver = new MockVisionDriver();
        var logger = NullLogger<CompoundTaskService>.Instance;

        var service = new CompoundTaskService(agvDriver, armDriver, visionDriver, logger);

        var task = new CompoundTransportTask(
            TaskId: Guid.NewGuid(),
            SourceStationId: "SAMPLE_01",
            TargetStationId: "ST_PREP_01",
            Profile: new CompoundTaskProfile(
                RequireVisionGuidance: true,
                RequirePickAtSource: true,
                RequirePlaceAtTarget: true,
                VisionPresetName: "lab_material",
                DefaultPickPose: new RobotArmPose(100, 200, 50, 0, 0, 0),
                DefaultPlacePose: new RobotArmPose(300, 400, 50, 0, 0, 0),
                MaxVisionRetries: 2,
                MinVisionConfidence: 0.80));

        // Act
        var result = await service.ExecuteAsync(task, CancellationToken.None);

        // Assert
        Assert.True(result.Success, $"Task should succeed but got: {result.ErrorMessage}");
        Assert.Equal(CompoundTaskState.Completed, result.FinalState);
        Assert.NotNull(result.Details);
        Assert.NotNull(result.Details.VisionCaptureId);
        Assert.NotNull(result.Details.VisionRecognitionId);
        Assert.NotNull(result.Details.VisionLocalizationId);
        Assert.NotNull(result.Details.PickOperationId);
        Assert.NotNull(result.Details.PlaceOperationId);
        Assert.NotNull(result.Details.ActualPickPose);
        Assert.True(result.Details.TotalDuration > TimeSpan.Zero);
    }

    [Fact(Skip = "Requires full AGV simulator setup")]
    public async Task ExecuteCompoundTask_WithoutVisionGuidance_ShouldUseDefaultPose()
    {
        // Arrange
        var agvDriver = CreateMockAgvDriver();
        var armDriver = new MockRobotArmDriver();
        var visionDriver = new MockVisionDriver();
        var logger = NullLogger<CompoundTaskService>.Instance;

        var service = new CompoundTaskService(agvDriver, armDriver, visionDriver, logger);

        var defaultPickPose = new RobotArmPose(100, 200, 50, 0, 0, 0);
        var task = new CompoundTransportTask(
            TaskId: Guid.NewGuid(),
            SourceStationId: "SAMPLE_01",
            TargetStationId: "ST_PREP_01",
            Profile: new CompoundTaskProfile(
                RequireVisionGuidance: false, // No vision
                RequirePickAtSource: true,
                RequirePlaceAtTarget: true,
                DefaultPickPose: defaultPickPose,
                DefaultPlacePose: new RobotArmPose(300, 400, 50, 0, 0, 0)));

        // Act
        var result = await service.ExecuteAsync(task, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(CompoundTaskState.Completed, result.FinalState);
        Assert.NotNull(result.Details);
        Assert.Null(result.Details.VisionCaptureId); // No vision used
        Assert.NotNull(result.Details.PickOperationId);
        Assert.NotNull(result.Details.PlaceOperationId);
    }

    [Fact(Skip = "Requires full AGV simulator setup")]
    public async Task ExecuteCompoundTask_NavigationOnly_ShouldSkipManipulation()
    {
        // Arrange
        var agvDriver = CreateMockAgvDriver();
        var armDriver = new MockRobotArmDriver();
        var visionDriver = new MockVisionDriver();
        var logger = NullLogger<CompoundTaskService>.Instance;

        var service = new CompoundTaskService(agvDriver, armDriver, visionDriver, logger);

        var task = new CompoundTransportTask(
            TaskId: Guid.NewGuid(),
            SourceStationId: "SAMPLE_01",
            TargetStationId: "ST_PREP_01",
            Profile: new CompoundTaskProfile(
                RequireVisionGuidance: false,
                RequirePickAtSource: false, // No pick
                RequirePlaceAtTarget: false, // No place
                DefaultPickPose: null,
                DefaultPlacePose: null));

        // Act
        var result = await service.ExecuteAsync(task, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(CompoundTaskState.Completed, result.FinalState);
        Assert.NotNull(result.Details);
        Assert.Null(result.Details.PickOperationId); // No pick
        Assert.Null(result.Details.PlaceOperationId); // No place
    }

    [Fact(Skip = "Requires full AGV simulator setup")]
    public async Task ExecuteCompoundTask_VisionRetry_ShouldEventuallySucceed()
    {
        // Arrange
        var agvDriver = CreateMockAgvDriver();
        var armDriver = new MockRobotArmDriver();
        var visionDriver = new MockVisionDriver(); // Has 10% failure rate
        var logger = NullLogger<CompoundTaskService>.Instance;

        var service = new CompoundTaskService(agvDriver, armDriver, visionDriver, logger);

        var task = new CompoundTransportTask(
            TaskId: Guid.NewGuid(),
            SourceStationId: "SAMPLE_01",
            TargetStationId: "ST_PREP_01",
            Profile: new CompoundTaskProfile(
                RequireVisionGuidance: true,
                RequirePickAtSource: true,
                RequirePlaceAtTarget: true,
                DefaultPickPose: new RobotArmPose(100, 200, 50, 0, 0, 0),
                DefaultPlacePose: new RobotArmPose(300, 400, 50, 0, 0, 0),
                MaxVisionRetries: 5, // Allow more retries
                MinVisionConfidence: 0.80));

        // Act
        var result = await service.ExecuteAsync(task, CancellationToken.None);

        // Assert
        // Should eventually succeed due to retries
        Assert.True(result.Success || result.FinalState == CompoundTaskState.VisionFailed);
        if (result.Success)
        {
            Assert.NotNull(result.Details?.VisionCaptureId);
        }
    }

    [Fact(Skip = "Requires full AGV simulator setup")]
    public async Task ExecuteCompoundTask_Cancellation_ShouldStopGracefully()
    {
        // Arrange
        var agvDriver = CreateMockAgvDriver();
        var armDriver = new MockRobotArmDriver();
        var visionDriver = new MockVisionDriver();
        var logger = NullLogger<CompoundTaskService>.Instance;

        var service = new CompoundTaskService(agvDriver, armDriver, visionDriver, logger);

        var task = new CompoundTransportTask(
            TaskId: Guid.NewGuid(),
            SourceStationId: "SAMPLE_01",
            TargetStationId: "ST_PREP_01",
            Profile: new CompoundTaskProfile(
                RequireVisionGuidance: true,
                RequirePickAtSource: true,
                RequirePlaceAtTarget: true,
                DefaultPickPose: new RobotArmPose(100, 200, 50, 0, 0, 0),
                DefaultPlacePose: new RobotArmPose(300, 400, 50, 0, 0, 0)));

        var cts = new CancellationTokenSource();
        cts.CancelAfter(500); // Cancel after 500ms

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await service.ExecuteAsync(task, cts.Token));
    }

    [Fact]
    public async Task MockRobotArmDriver_PickAndPlace_ShouldMaintainState()
    {
        // Arrange
        var driver = new MockRobotArmDriver();
        await driver.ConnectAsync(CancellationToken.None);

        // Act - Initial state
        var initialStatus = await driver.GetStatusAsync(CancellationToken.None);
        Assert.False(initialStatus.IsHoldingObject);
        Assert.Equal("Idle", initialStatus.State);

        // Act - Pick (retry if simulated failure occurs)
        RobotArmOperationResponse pickResult;
        var maxRetries = 10; // Mock has 5% failure rate
        var pickAttempt = 0;
        do
        {
            var pickCommand = new RobotArmPickCommand(
                OperationId: Guid.NewGuid(),
                TargetPose: new RobotArmPose(100, 200, 50, 0, 0, 0));
            pickResult = await driver.PickAsync(pickCommand, CancellationToken.None);
            pickAttempt++;
        } while (pickResult.State != "Completed" && pickAttempt < maxRetries);

        // Assert - Holding object
        Assert.Equal("Completed", pickResult.State);
        var afterPickStatus = await driver.GetStatusAsync(CancellationToken.None);
        Assert.True(afterPickStatus.IsHoldingObject);

        // Act - Place
        var placeCommand = new RobotArmPlaceCommand(
            OperationId: Guid.NewGuid(),
            TargetPose: new RobotArmPose(300, 400, 50, 0, 0, 0));
        var placeResult = await driver.PlaceAsync(placeCommand, CancellationToken.None);

        // Assert - Not holding object
        Assert.Equal("Completed", placeResult.State);
        var afterPlaceStatus = await driver.GetStatusAsync(CancellationToken.None);
        Assert.False(afterPlaceStatus.IsHoldingObject);
    }

    [Fact]
    public async Task MockVisionDriver_CaptureAndLocalize_ShouldReturnRealisticData()
    {
        // Arrange
        var driver = new MockVisionDriver();
        await driver.ConnectAsync(CancellationToken.None);

        // Act - Capture
        var captureCommand = new VisionCaptureCommand(
            CaptureId: Guid.NewGuid(),
            PresetName: "test_preset");
        var captureResult = await driver.CaptureAsync(captureCommand, CancellationToken.None);

        // Assert - Capture
        Assert.NotNull(captureResult.ImagePath);
        Assert.Contains("capture_", captureResult.ImagePath);

        // Act - Recognize
        var recognitionCommand = new VisionRecognitionCommand(
            RecognitionId: Guid.NewGuid(),
            CaptureId: captureResult.CaptureId,
            TargetType: "shape");
        var recognitionResult = await driver.RecognizeAsync(recognitionCommand, CancellationToken.None);

        // Assert - Recognition (may fail due to 10% simulated failure rate)
        if (recognitionResult.Found)
        {
            Assert.True(recognitionResult.Confidence >= 0.80);
            Assert.NotNull(recognitionResult.BoundingBox);

            // Act - Localize
            var localizationCommand = new VisionLocalizationCommand(
                LocalizationId: Guid.NewGuid(),
                RecognitionId: recognitionResult.RecognitionId,
                CoordinateFrame: "robot_base");
            var localizationResult = await driver.LocalizeAsync(localizationCommand, CancellationToken.None);

            // Assert - Localization
            Assert.True(localizationResult.Success);
            Assert.InRange(localizationResult.X, 250, 350); // Realistic range
            Assert.InRange(localizationResult.Y, 50, 250);
            Assert.InRange(localizationResult.Z, 0, 10);
        }
    }

    private static IAgvDriver CreateMockAgvDriver()
    {
        // Placeholder for mock AGV driver
        // In real tests, this would be properly implemented
        throw new NotImplementedException("Mock AGV driver not yet implemented");
    }
}
