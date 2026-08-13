extern alias AdapterApp;
using AdapterApp::MesControlAgv.Adapter.Drivers;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using MesControlAgv.Mes.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MesControlAgv.E2E.Tests;

/// <summary>
/// Integration tests for CompoundTaskServiceV2 with enhanced features:
/// device validation, rollback, concurrency control, and progress reporting.
/// </summary>
public sealed class CompoundTaskServiceV2Tests
{
    [Fact]
    public async Task V2_DevicePreCheck_ShouldFailIfRobotArmNotReady()
    {
        // Arrange
        var armDriver = new FaultyRobotArmDriver(); // Not connected
        var visionDriver = new MockVisionDriver();
        var agvDriver = new MockAgvDriverForTesting();

        var options = Options.Create(new CompoundTaskOptions
        {
            EnableDevicePreCheck = true
        });

        var service = new CompoundTaskServiceV2(
            agvDriver, armDriver, visionDriver, options,
            NullLogger<CompoundTaskServiceV2>.Instance);

        var task = new CompoundTransportTask(
            TaskId: Guid.NewGuid(),
            SourceStationId: "SAMPLE_01",
            TargetStationId: "ST_PREP_01",
            Profile: new CompoundTaskProfile(
                RequireVisionGuidance: false,
                RequirePickAtSource: true,
                RequirePlaceAtTarget: true,
                DefaultPickPose: new RobotArmPose(100, 200, 50, 0, 0, 0),
                DefaultPlacePose: new RobotArmPose(300, 400, 50, 0, 0, 0)));

        // Act
        var result = await service.ExecuteAsync(task, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not connected", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task V2_ConcurrencyControl_ShouldRejectSecondTask()
    {
        // Arrange
        var armDriver = new MockRobotArmDriver();
        var visionDriver = new MockVisionDriver();
        var agvDriver = new MockAgvDriverForTesting();

        var options = Options.Create(new CompoundTaskOptions
        {
            EnableDevicePreCheck = false, // Skip for speed
            AgvNavigationTimeout = TimeSpan.FromSeconds(10)
        });

        var service = new CompoundTaskServiceV2(
            agvDriver, armDriver, visionDriver, options,
            NullLogger<CompoundTaskServiceV2>.Instance);

        var task1 = new CompoundTransportTask(
            TaskId: Guid.NewGuid(),
            SourceStationId: "SAMPLE_01",
            TargetStationId: "ST_PREP_01",
            Profile: new CompoundTaskProfile(
                RequireVisionGuidance: false,
                RequirePickAtSource: true,
                RequirePlaceAtTarget: true,
                DefaultPickPose: new RobotArmPose(100, 200, 50, 0, 0, 0),
                DefaultPlacePose: new RobotArmPose(300, 400, 50, 0, 0, 0)));

        var task2 = new CompoundTransportTask(
            TaskId: Guid.NewGuid(),
            SourceStationId: "SAMPLE_01",
            TargetStationId: "ST_PREP_01",
            Profile: task1.Profile);

        // Act - Start task1
        var task1Execution = service.ExecuteAsync(task1, CancellationToken.None);
        await Task.Delay(100); // Let task1 acquire the lock

        // Act - Try to start task2
        var exception = await Assert.ThrowsAsync<CompoundTaskException>(
            async () => await service.ExecuteAsync(task2, CancellationToken.None));

        // Assert
        Assert.Contains("already executing", exception.Message, StringComparison.OrdinalIgnoreCase);

        // Cleanup - wait for task1 to complete
        await task1Execution;
    }

    [Fact]
    public async Task V2_ConfigurableTimeouts_ShouldRespectSettings()
    {
        // Arrange
        var armDriver = new MockRobotArmDriver();
        var visionDriver = new MockVisionDriver();
        var agvDriver = new SlowAgvDriver(); // Never arrives

        var options = Options.Create(new CompoundTaskOptions
        {
            EnableDevicePreCheck = false,
            AgvNavigationTimeout = TimeSpan.FromSeconds(2), // Very short timeout
            ProgressReportInterval = TimeSpan.FromSeconds(1)
        });

        var service = new CompoundTaskServiceV2(
            agvDriver, armDriver, visionDriver, options,
            NullLogger<CompoundTaskServiceV2>.Instance);

        var task = new CompoundTransportTask(
            TaskId: Guid.NewGuid(),
            SourceStationId: "SAMPLE_01",
            TargetStationId: "ST_PREP_01",
            Profile: new CompoundTaskProfile(
                RequireVisionGuidance: false,
                RequirePickAtSource: false,
                RequirePlaceAtTarget: false));

        // Act
        var startTime = DateTimeOffset.UtcNow;
        var result = await service.ExecuteAsync(task, CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - startTime;

        // Assert
        Assert.False(result.Success);
        Assert.True(elapsed < TimeSpan.FromSeconds(3),
            $"Should timeout quickly, but took {elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task V2_RollbackMechanism_ShouldPlaceBackOnFailure()
    {
        // Arrange
        var armDriver = new MockRobotArmDriver();
        var visionDriver = new MockVisionDriver();
        var agvDriver = new FailingAfterPickAgvDriver(); // Fails after pick

        var options = Options.Create(new CompoundTaskOptions
        {
            EnableDevicePreCheck = false,
            EnableRollbackOnFailure = true,
            AgvNavigationTimeout = TimeSpan.FromSeconds(3)
        });

        var service = new CompoundTaskServiceV2(
            agvDriver, armDriver, visionDriver, options,
            NullLogger<CompoundTaskServiceV2>.Instance);

        var defaultPose = new RobotArmPose(100, 200, 50, 0, 0, 0);
        var task = new CompoundTransportTask(
            TaskId: Guid.NewGuid(),
            SourceStationId: "SAMPLE_01",
            TargetStationId: "ST_PREP_01",
            Profile: new CompoundTaskProfile(
                RequireVisionGuidance: false,
                RequirePickAtSource: true,
                RequirePlaceAtTarget: true,
                DefaultPickPose: defaultPose,
                DefaultPlacePose: new RobotArmPose(300, 400, 50, 0, 0, 0)));

        // Act
        var result = await service.ExecuteAsync(task, CancellationToken.None);

        // Assert
        Assert.False(result.Success);

        // Check robot arm state after rollback
        var armStatus = await armDriver.GetStatusAsync(CancellationToken.None);
        Assert.False(armStatus.IsHoldingObject, "Robot arm should not be holding object after rollback");
    }

    [Fact]
    public async Task V2_SuccessfulExecution_ShouldCompleteAllPhases()
    {
        // Arrange
        var armDriver = new MockRobotArmDriver();
        var visionDriver = new MockVisionDriver();
        var agvDriver = new MockAgvDriverForTesting();

        var options = Options.Create(new CompoundTaskOptions
        {
            EnableDevicePreCheck = true,
            EnableRollbackOnFailure = true,
            AgvNavigationTimeout = TimeSpan.FromMinutes(1)
        });

        var service = new CompoundTaskServiceV2(
            agvDriver, armDriver, visionDriver, options,
            NullLogger<CompoundTaskServiceV2>.Instance);

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
                MaxVisionRetries: 3,
                MinVisionConfidence: 0.80));

        // Act
        var result = await service.ExecuteAsync(task, CancellationToken.None);

        // Assert
        Assert.True(result.Success, $"Task should succeed but failed: {result.ErrorMessage}");
        Assert.Equal(CompoundTaskState.Completed, result.FinalState);
        Assert.NotNull(result.Details);
        Assert.NotNull(result.Details.VisionCaptureId);
        Assert.NotNull(result.Details.PickOperationId);
        Assert.NotNull(result.Details.PlaceOperationId);
        Assert.True(result.Details.TotalDuration > TimeSpan.Zero);

        // Check robot arm is back to idle
        var armStatus = await armDriver.GetStatusAsync(CancellationToken.None);
        Assert.Equal("Idle", armStatus.State);
        Assert.False(armStatus.IsHoldingObject);
    }

    // Helper drivers for testing

    private sealed class FaultyRobotArmDriver : IRobotArmDriver
    {
        public string DriverId => "faulty-arm";
        public RobotArmCapabilities Capabilities => new(false, false, false, 0, 0);

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<RobotArmStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new RobotArmStatusResponse(
                State: "Error",
                IsConnected: false, // Not connected
                IsEnabled: false,
                HasError: true,
                ErrorMessage: "Simulated connection failure",
                CurrentPose: null,
                IsHoldingObject: false,
                GripperForceNewton: null));
        }

        public Task<RobotArmOperationResponse> PickAsync(RobotArmPickCommand command, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<RobotArmOperationResponse> PlaceAsync(RobotArmPlaceCommand command, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<RobotArmOperationResponse> MoveToAsync(RobotArmMoveCommand command, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<RobotArmOperationResponse> HomeAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task EmergencyStopAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class MockAgvDriverForTesting : IAgvDriver
    {
        private string _currentLocation = "CHARGE_01";

        public string DriverId => "mock-agv";
        public AgvCapabilitiesResponse Capabilities => AgvCapabilitiesResponse.Standard;

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<AgvSnapshotResponse> GetSnapshotAsync(string agvId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AgvSnapshotResponse(
                Online: true,
                ControlOwner: "TEST",
                CurrentStationId: _currentLocation,
                CurrentTaskId: null,
                AgvId: agvId));
        }

        public async Task<AgvTaskResponse> DispatchAsync(AgvDispatchCommand command, CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken); // Simulate movement
            _currentLocation = command.TargetStationId;

            return new AgvTaskResponse(
                TaskId: command.TaskId,
                DeviceTaskId: command.TaskId.ToString(),
                TargetStationId: command.TargetStationId,
                State: "Completed",
                LastError: null,
                AgvId: command.AgvId);
        }

        public Task<AgvTaskResponse?> PauseAsync(AgvControlCommand command, CancellationToken cancellationToken)
            => Task.FromResult<AgvTaskResponse?>(null);
        public Task<AgvTaskResponse?> ResumeAsync(AgvControlCommand command, CancellationToken cancellationToken)
            => Task.FromResult<AgvTaskResponse?>(null);
        public Task<AgvTaskResponse?> CancelAsync(AgvControlCommand command, CancellationToken cancellationToken)
            => Task.FromResult<AgvTaskResponse?>(null);
    }

    private sealed class SlowAgvDriver : IAgvDriver
    {
        public string DriverId => "slow-agv";
        public AgvCapabilitiesResponse Capabilities => AgvCapabilitiesResponse.Standard;

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<AgvSnapshotResponse> GetSnapshotAsync(string agvId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AgvSnapshotResponse(
                Online: true,
                ControlOwner: "TEST",
                CurrentStationId: "SOMEWHERE", // Never arrives
                CurrentTaskId: null,
                AgvId: agvId));
        }

        public Task<AgvTaskResponse> DispatchAsync(AgvDispatchCommand command, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AgvTaskResponse(
                TaskId: command.TaskId,
                DeviceTaskId: command.TaskId.ToString(),
                TargetStationId: command.TargetStationId,
                State: "Moving",
                LastError: null,
                AgvId: command.AgvId));
        }

        public Task<AgvTaskResponse?> PauseAsync(AgvControlCommand command, CancellationToken cancellationToken)
            => Task.FromResult<AgvTaskResponse?>(null);
        public Task<AgvTaskResponse?> ResumeAsync(AgvControlCommand command, CancellationToken cancellationToken)
            => Task.FromResult<AgvTaskResponse?>(null);
        public Task<AgvTaskResponse?> CancelAsync(AgvControlCommand command, CancellationToken cancellationToken)
            => Task.FromResult<AgvTaskResponse?>(null);
    }

    private sealed class FailingAfterPickAgvDriver : IAgvDriver
    {
        private string _currentLocation = "CHARGE_01";
        private int _dispatchCount = 0;

        public string DriverId => "failing-agv";
        public AgvCapabilitiesResponse Capabilities => AgvCapabilitiesResponse.Standard;

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<AgvSnapshotResponse> GetSnapshotAsync(string agvId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new AgvSnapshotResponse(
                Online: true,
                ControlOwner: "TEST",
                CurrentStationId: _currentLocation,
                CurrentTaskId: null,
                AgvId: agvId));
        }

        public async Task<AgvTaskResponse> DispatchAsync(AgvDispatchCommand command, CancellationToken cancellationToken)
        {
            _dispatchCount++;

            if (_dispatchCount == 1)
            {
                // First navigation (to source) succeeds
                await Task.Delay(100, cancellationToken);
                _currentLocation = command.TargetStationId;

                return new AgvTaskResponse(
                    TaskId: command.TaskId,
                    DeviceTaskId: command.TaskId.ToString(),
                    TargetStationId: command.TargetStationId,
                    State: "Completed",
                    LastError: null,
                    AgvId: command.AgvId);
            }
            else if (_dispatchCount == 2)
            {
                // Second navigation (to target) fails - triggers rollback
                throw new InvalidOperationException("Simulated AGV navigation failure");
            }
            else
            {
                // Rollback navigation (back to source) succeeds
                await Task.Delay(100, cancellationToken);
                _currentLocation = command.TargetStationId;

                return new AgvTaskResponse(
                    TaskId: command.TaskId,
                    DeviceTaskId: command.TaskId.ToString(),
                    TargetStationId: command.TargetStationId,
                    State: "Completed",
                    LastError: null,
                    AgvId: command.AgvId);
            }
        }

        public Task<AgvTaskResponse?> PauseAsync(AgvControlCommand command, CancellationToken cancellationToken)
            => Task.FromResult<AgvTaskResponse?>(null);
        public Task<AgvTaskResponse?> ResumeAsync(AgvControlCommand command, CancellationToken cancellationToken)
            => Task.FromResult<AgvTaskResponse?>(null);
        public Task<AgvTaskResponse?> CancelAsync(AgvControlCommand command, CancellationToken cancellationToken)
            => Task.FromResult<AgvTaskResponse?>(null);
    }
}
