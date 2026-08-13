using MesControlAgv.Application;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Drivers;

/// <summary>
/// Mock robot arm driver for development and testing without physical hardware.
/// Simulates realistic behavior including delays, state transitions, and occasional failures.
/// </summary>
public sealed class MockRobotArmDriver : IRobotArmDriver
{
    public const string DriverKind = "mock-robot-arm";

    private readonly RobotArmDriverOptions _options;
    private readonly Random _random = new();
    private RobotArmPose _currentPose;
    private bool _isHoldingObject;
    private string _state = "Idle";
    private readonly object _lock = new();

    public MockRobotArmDriver(RobotArmDriverOptions? options = null)
    {
        _options = options ?? new RobotArmDriverOptions();
        _currentPose = new RobotArmPose(0, 0, 300, 0, 0, 0); // Home position
        _isHoldingObject = false;
    }

    public string DriverId => DriverKind;

    public RobotArmCapabilities Capabilities => new(
        SupportsForceControl: true,
        SupportsVisionGuidance: true,
        SupportsDragTeaching: true,
        MaxPayloadGrams: 3000,
        ReachMillimeters: 650);

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        // Simulate connection delay
        return Task.Delay(100, cancellationToken);
    }

    public Task<RobotArmStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(new RobotArmStatusResponse(
                State: _state,
                IsConnected: true,
                IsEnabled: true,
                HasError: false,
                ErrorMessage: null,
                CurrentPose: _currentPose,
                IsHoldingObject: _isHoldingObject,
                GripperForceNewton: _isHoldingObject ? 30 : 0,
                ArmId: _options.DefaultArmId));
        }
    }

    public async Task<RobotArmOperationResponse> PickAsync(
        RobotArmPickCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        lock (_lock)
        {
            if (_isHoldingObject)
            {
                return new RobotArmOperationResponse(
                    OperationId: command.OperationId,
                    State: "Failed",
                    ErrorMessage: "Robot arm is already holding an object",
                    CompletedAt: DateTimeOffset.UtcNow,
                    ArmId: _options.DefaultArmId);
            }

            _state = "Moving";
        }

        try
        {
            // Simulate approach movement
            if (command.ApproachHeightMm.HasValue)
            {
                var approachPose = command.TargetPose with
                {
                    Z = command.TargetPose.Z + command.ApproachHeightMm.Value
                };
                await SimulateMoveToAsync(approachPose, cancellationToken);
            }

            // Simulate move to target
            await SimulateMoveToAsync(command.TargetPose, cancellationToken);

            // Simulate gripper closing
            await Task.Delay(500, cancellationToken);

            // Simulate occasional pick failure (5% chance)
            if (_random.Next(100) < 5)
            {
                lock (_lock)
                {
                    _state = "Idle";
                }
                return new RobotArmOperationResponse(
                    OperationId: command.OperationId,
                    State: "Failed",
                    ErrorMessage: "Failed to grip object (simulated failure)",
                    CompletedAt: DateTimeOffset.UtcNow,
                    ArmId: _options.DefaultArmId);
            }

            lock (_lock)
            {
                _isHoldingObject = true;
                _currentPose = command.TargetPose;
                _state = "Idle";
            }

            return new RobotArmOperationResponse(
                OperationId: command.OperationId,
                State: "Completed",
                ErrorMessage: null,
                CompletedAt: DateTimeOffset.UtcNow,
                ArmId: _options.DefaultArmId);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _state = "Idle";
            }
            throw;
        }
    }

    public async Task<RobotArmOperationResponse> PlaceAsync(
        RobotArmPlaceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        lock (_lock)
        {
            if (!_isHoldingObject)
            {
                return new RobotArmOperationResponse(
                    OperationId: command.OperationId,
                    State: "Failed",
                    ErrorMessage: "Robot arm is not holding any object",
                    CompletedAt: DateTimeOffset.UtcNow,
                    ArmId: _options.DefaultArmId);
            }

            _state = "Moving";
        }

        try
        {
            // Simulate approach movement
            if (command.ApproachHeightMm.HasValue)
            {
                var approachPose = command.TargetPose with
                {
                    Z = command.TargetPose.Z + command.ApproachHeightMm.Value
                };
                await SimulateMoveToAsync(approachPose, cancellationToken);
            }

            // Simulate move to target
            await SimulateMoveToAsync(command.TargetPose, cancellationToken);

            // Simulate gripper opening with delay
            await Task.Delay(command.ReleaseDelayMs ?? 500, cancellationToken);

            lock (_lock)
            {
                _isHoldingObject = false;
                _currentPose = command.TargetPose;
                _state = "Idle";
            }

            return new RobotArmOperationResponse(
                OperationId: command.OperationId,
                State: "Completed",
                ErrorMessage: null,
                CompletedAt: DateTimeOffset.UtcNow,
                ArmId: _options.DefaultArmId);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _state = "Idle";
            }
            throw;
        }
    }

    public async Task<RobotArmOperationResponse> MoveToAsync(
        RobotArmMoveCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        lock (_lock)
        {
            _state = "Moving";
        }

        try
        {
            await SimulateMoveToAsync(command.TargetPose, cancellationToken);

            lock (_lock)
            {
                _currentPose = command.TargetPose;
                _state = "Idle";
            }

            return new RobotArmOperationResponse(
                OperationId: command.OperationId,
                State: "Completed",
                ErrorMessage: null,
                CompletedAt: DateTimeOffset.UtcNow,
                ArmId: _options.DefaultArmId);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _state = "Idle";
            }
            throw;
        }
    }

    public async Task<RobotArmOperationResponse> HomeAsync(CancellationToken cancellationToken)
    {
        var homeCommand = new RobotArmMoveCommand(
            OperationId: Guid.NewGuid(),
            TargetPose: new RobotArmPose(0, 0, 300, 0, 0, 0),
            ArmId: _options.DefaultArmId);

        return await MoveToAsync(homeCommand, cancellationToken);
    }

    public Task EmergencyStopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _state = "EmergencyStopped";
        }
        return Task.CompletedTask;
    }

    private async Task SimulateMoveToAsync(RobotArmPose targetPose, CancellationToken cancellationToken)
    {
        // Calculate distance for realistic delay
        RobotArmPose currentPose;
        lock (_lock)
        {
            currentPose = _currentPose;
        }

        var distance = Math.Sqrt(
            Math.Pow(targetPose.X - currentPose.X, 2) +
            Math.Pow(targetPose.Y - currentPose.Y, 2) +
            Math.Pow(targetPose.Z - currentPose.Z, 2));

        // Simulate movement time: ~1 second per 100mm
        var movementTimeMs = (int)(distance / 100.0 * 1000);
        movementTimeMs = Math.Clamp(movementTimeMs, 200, 5000);

        await Task.Delay(movementTimeMs, cancellationToken);
    }
}

public sealed class MockRobotArmDriverFactory : IRobotArmDriverFactory
{
    public string DriverId => MockRobotArmDriver.DriverKind;

    public IRobotArmDriver Create(RobotArmDriverOptions options) =>
        new MockRobotArmDriver(options);
}
