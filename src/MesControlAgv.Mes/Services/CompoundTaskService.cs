using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using Microsoft.Extensions.Logging;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Compound task orchestration service that coordinates AGV, robot arm, and vision system
/// to execute complex transport tasks with visual guidance and manipulation.
/// </summary>
public sealed class CompoundTaskService
{
    private readonly IAgvDriver _agvDriver;
    private readonly IRobotArmDriver _armDriver;
    private readonly IVisionDriver _visionDriver;
    private readonly ILogger<CompoundTaskService> _logger;

    public CompoundTaskService(
        IAgvDriver agvDriver,
        IRobotArmDriver armDriver,
        IVisionDriver visionDriver,
        ILogger<CompoundTaskService> logger)
    {
        _agvDriver = agvDriver ?? throw new ArgumentNullException(nameof(agvDriver));
        _armDriver = armDriver ?? throw new ArgumentNullException(nameof(armDriver));
        _visionDriver = visionDriver ?? throw new ArgumentNullException(nameof(visionDriver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Execute a compound transport task with full orchestration of all devices.
    /// </summary>
    public async Task<CompoundTaskResult> ExecuteAsync(
        CompoundTransportTask task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        var startedAt = DateTimeOffset.UtcNow;
        var state = CompoundTaskState.Created;
        var details = new CompoundTaskExecutionDetails(
            StartedAt: startedAt,
            CompletedAt: startedAt,
            TotalDuration: TimeSpan.Zero);

        _logger.LogInformation(
            "Starting compound task {TaskId}: {Source} -> {Target}",
            task.TaskId, task.SourceStationId, task.TargetStationId);

        try
        {
            // Phase 1: Navigate AGV to source station
            state = CompoundTaskState.NavigatingToSource;
            var navTaskId = await NavigateToStationAsync(
                task.TaskId,
                task.SourceStationId,
                cancellationToken);
            details = details with { AgvNavigationTaskId = navTaskId };

            state = CompoundTaskState.ArrivedAtSource;
            _logger.LogInformation("Task {TaskId}: AGV arrived at source station", task.TaskId);

            // Phase 2: Vision guidance (if required)
            RobotArmPose pickPose = task.Profile.DefaultPickPose
                ?? throw new InvalidOperationException("Default pick pose is required");

            if (task.Profile.RequireVisionGuidance)
            {
                state = CompoundTaskState.VisionScanning;
                var visionResult = await ExecuteVisionGuidanceAsync(task, cancellationToken);

                if (!visionResult.Success)
                {
                    state = CompoundTaskState.VisionFailed;
                    throw new InvalidOperationException($"Vision guidance failed: {visionResult.ErrorMessage}");
                }

                pickPose = visionResult.CorrectedPose!;
                details = details with
                {
                    VisionCaptureId = visionResult.CaptureId,
                    VisionRecognitionId = visionResult.RecognitionId,
                    VisionLocalizationId = visionResult.LocalizationId,
                    VisionRetryCount = visionResult.RetryCount,
                    ActualPickPose = pickPose
                };

                state = CompoundTaskState.VisionCompleted;
                _logger.LogInformation(
                    "Task {TaskId}: Vision completed, pick pose corrected to ({X}, {Y}, {Z})",
                    task.TaskId, pickPose.X, pickPose.Y, pickPose.Z);
            }

            // Phase 3: Robot arm pick (if required)
            if (task.Profile.RequirePickAtSource)
            {
                state = CompoundTaskState.Picking;
                var pickOpId = await ExecutePickAsync(pickPose, cancellationToken);
                details = details with { PickOperationId = pickOpId };

                state = CompoundTaskState.PickCompleted;
                _logger.LogInformation("Task {TaskId}: Pick operation completed", task.TaskId);
            }

            // Phase 4: Navigate AGV to target station
            state = CompoundTaskState.NavigatingToTarget;
            await NavigateToStationAsync(task.TaskId, task.TargetStationId, cancellationToken);

            state = CompoundTaskState.ArrivedAtTarget;
            _logger.LogInformation("Task {TaskId}: AGV arrived at target station", task.TaskId);

            // Phase 5: Robot arm place (if required)
            if (task.Profile.RequirePlaceAtTarget)
            {
                state = CompoundTaskState.Placing;
                var placePose = task.Profile.DefaultPlacePose
                    ?? throw new InvalidOperationException("Default place pose is required");
                var placeOpId = await ExecutePlaceAsync(placePose, cancellationToken);
                details = details with { PlaceOperationId = placeOpId };

                state = CompoundTaskState.PlaceCompleted;
                _logger.LogInformation("Task {TaskId}: Place operation completed", task.TaskId);
            }

            // Phase 6: Return robot arm to home
            state = CompoundTaskState.ReturningHome;
            await _armDriver.HomeAsync(cancellationToken);

            // Task completed
            state = CompoundTaskState.Completed;
            var completedAt = DateTimeOffset.UtcNow;
            details = details with
            {
                CompletedAt = completedAt,
                TotalDuration = completedAt - startedAt
            };

            _logger.LogInformation(
                "Task {TaskId}: Completed successfully in {Duration:F1}s",
                task.TaskId, details.TotalDuration.TotalSeconds);

            return new CompoundTaskResult(
                TaskId: task.TaskId,
                FinalState: state,
                Success: true,
                ErrorMessage: null,
                Details: details);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Task {TaskId} failed at state {State}: {Error}",
                task.TaskId, state, ex.Message);

            var completedAt = DateTimeOffset.UtcNow;
            details = details with
            {
                CompletedAt = completedAt,
                TotalDuration = completedAt - startedAt
            };

            return new CompoundTaskResult(
                TaskId: task.TaskId,
                FinalState: CompoundTaskState.Failed,
                Success: false,
                ErrorMessage: ex.Message,
                Details: details);
        }
    }

    private async Task<Guid> NavigateToStationAsync(
        Guid taskId,
        string stationId,
        CancellationToken cancellationToken)
    {
        var navCommand = new AgvDispatchCommand(
            TaskId: taskId,
            AgvId: "AGV-01",
            TargetStationId: stationId);

        _logger.LogDebug("Dispatching AGV to station {StationId}", stationId);
        var navResult = await _agvDriver.DispatchAsync(navCommand, cancellationToken);

        // Poll for arrival
        await WaitForAgvArrivalAsync(taskId, stationId, cancellationToken);

        return taskId;
    }

    private async Task WaitForAgvArrivalAsync(
        Guid taskId,
        string expectedStation,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMinutes(5);
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var snapshot = await _agvDriver.GetSnapshotAsync("AGV-01", cancellationToken);

            if (snapshot.CurrentStationId == expectedStation)
            {
                _logger.LogDebug("AGV arrived at station {StationId}", expectedStation);
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException($"AGV did not arrive at station {expectedStation} within {timeout}");
    }

    private async Task<VisionGuidanceResult> ExecuteVisionGuidanceAsync(
        CompoundTransportTask task,
        CancellationToken cancellationToken)
    {
        var maxRetries = task.Profile.MaxVisionRetries;
        var minConfidence = task.Profile.MinVisionConfidence;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                _logger.LogWarning(
                    "Task {TaskId}: Vision retry {Attempt}/{Max}",
                    task.TaskId, attempt, maxRetries);
                await Task.Delay(1000, cancellationToken); // Brief delay before retry
            }

            try
            {
                // Step 1: Capture image
                var captureId = Guid.NewGuid();
                var captureCommand = new VisionCaptureCommand(
                    CaptureId: captureId,
                    PresetName: task.Profile.VisionPresetName ?? "default");

                var captureResult = await _visionDriver.CaptureAsync(captureCommand, cancellationToken);
                _logger.LogDebug("Vision captured image: {ImagePath}", captureResult.ImagePath);

                // Step 2: Recognize object
                var recognitionId = Guid.NewGuid();
                var recognitionCommand = new VisionRecognitionCommand(
                    RecognitionId: recognitionId,
                    CaptureId: captureId,
                    TargetType: "shape");

                var recognitionResult = await _visionDriver.RecognizeAsync(recognitionCommand, cancellationToken);

                if (!recognitionResult.Found)
                {
                    _logger.LogWarning(
                        "Task {TaskId}: Object not found in vision (confidence: {Confidence:F2})",
                        task.TaskId, recognitionResult.Confidence);
                    continue; // Retry
                }

                if (recognitionResult.Confidence < minConfidence)
                {
                    _logger.LogWarning(
                        "Task {TaskId}: Vision confidence too low: {Confidence:F2} < {Min:F2}",
                        task.TaskId, recognitionResult.Confidence, minConfidence);
                    continue; // Retry
                }

                // Step 3: Localize object in 3D
                var localizationId = Guid.NewGuid();
                var localizationCommand = new VisionLocalizationCommand(
                    LocalizationId: localizationId,
                    RecognitionId: recognitionId,
                    CoordinateFrame: "robot_base");

                var localizationResult = await _visionDriver.LocalizeAsync(localizationCommand, cancellationToken);

                if (!localizationResult.Success)
                {
                    _logger.LogWarning("Task {TaskId}: Vision localization failed", task.TaskId);
                    continue; // Retry
                }

                // Create corrected pose
                var defaultPose = task.Profile.DefaultPickPose!;
                var correctedPose = new RobotArmPose(
                    X: localizationResult.X,
                    Y: localizationResult.Y,
                    Z: localizationResult.Z,
                    Rx: defaultPose.Rx,
                    Ry: defaultPose.Ry,
                    Rz: localizationResult.RotationDegree ?? defaultPose.Rz);

                return new VisionGuidanceResult(
                    Success: true,
                    CorrectedPose: correctedPose,
                    CaptureId: captureId,
                    RecognitionId: recognitionId,
                    LocalizationId: localizationId,
                    RetryCount: attempt,
                    ErrorMessage: null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Task {TaskId}: Vision attempt {Attempt} failed", task.TaskId, attempt);
                if (attempt == maxRetries)
                    throw;
            }
        }

        return new VisionGuidanceResult(
            Success: false,
            CorrectedPose: null,
            CaptureId: null,
            RecognitionId: null,
            LocalizationId: null,
            RetryCount: maxRetries,
            ErrorMessage: $"Vision guidance failed after {maxRetries + 1} attempts");
    }

    private async Task<Guid> ExecutePickAsync(
        RobotArmPose pickPose,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var pickCommand = new RobotArmPickCommand(
            OperationId: operationId,
            TargetPose: pickPose,
            ApproachHeightMm: 50,
            GripperForceMn: 30);

        _logger.LogDebug("Executing pick at ({X}, {Y}, {Z})", pickPose.X, pickPose.Y, pickPose.Z);
        var result = await _armDriver.PickAsync(pickCommand, cancellationToken);

        if (result.State != "Completed")
        {
            throw new InvalidOperationException($"Pick operation failed: {result.ErrorMessage}");
        }

        return operationId;
    }

    private async Task<Guid> ExecutePlaceAsync(
        RobotArmPose placePose,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var placeCommand = new RobotArmPlaceCommand(
            OperationId: operationId,
            TargetPose: placePose,
            ApproachHeightMm: 50,
            ReleaseDelayMs: 500);

        _logger.LogDebug("Executing place at ({X}, {Y}, {Z})", placePose.X, placePose.Y, placePose.Z);
        var result = await _armDriver.PlaceAsync(placeCommand, cancellationToken);

        if (result.State != "Completed")
        {
            throw new InvalidOperationException($"Place operation failed: {result.ErrorMessage}");
        }

        return operationId;
    }

    private sealed record VisionGuidanceResult(
        bool Success,
        RobotArmPose? CorrectedPose,
        Guid? CaptureId,
        Guid? RecognitionId,
        Guid? LocalizationId,
        int RetryCount,
        string? ErrorMessage);
}
