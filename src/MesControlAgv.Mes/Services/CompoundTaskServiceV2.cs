using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Enhanced compound task orchestration service with device validation, rollback, and concurrency control.
/// Version 2.0 - Addresses code review findings.
/// </summary>
public sealed class CompoundTaskServiceV2 : IDisposable
{
    private readonly IAgvDriver _agvDriver;
    private readonly IRobotArmDriver _armDriver;
    private readonly IVisionDriver _visionDriver;
    private readonly ILogger<CompoundTaskServiceV2> _logger;
    private readonly CompoundTaskOptions _options;
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    public CompoundTaskServiceV2(
        IAgvDriver agvDriver,
        IRobotArmDriver armDriver,
        IVisionDriver visionDriver,
        IOptions<CompoundTaskOptions> options,
        ILogger<CompoundTaskServiceV2> logger)
    {
        _agvDriver = agvDriver ?? throw new ArgumentNullException(nameof(agvDriver));
        _armDriver = armDriver ?? throw new ArgumentNullException(nameof(armDriver));
        _visionDriver = visionDriver ?? throw new ArgumentNullException(nameof(visionDriver));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Execute a compound transport task with full orchestration, validation, and error recovery.
    /// </summary>
    public async Task<CompoundTaskResult> ExecuteAsync(
        CompoundTransportTask task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        // Acquire execution lock to prevent concurrent task conflicts
        if (!await _executionGate.WaitAsync(0, cancellationToken))
        {
            throw new CompoundTaskException(
                task.TaskId,
                CompoundTaskState.Failed,
                "Another compound task is already executing. Only one task can run at a time.");
        }

        try
        {
            return await ExecuteInternalAsync(task, cancellationToken);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private async Task<CompoundTaskResult> ExecuteInternalAsync(
        CompoundTransportTask task,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var state = CompoundTaskState.Created;
        var details = new CompoundTaskExecutionDetails(
            StartedAt: startedAt,
            CompletedAt: startedAt,
            TotalDuration: TimeSpan.Zero);

        var isHoldingObject = false;
        var lastKnownLocation = (string?)null;

        _logger.LogInformation(
            "Starting compound task {TaskId}: {Source} -> {Target} (Vision: {Vision}, Pick: {Pick}, Place: {Place})",
            task.TaskId, task.SourceStationId, task.TargetStationId,
            task.Profile.RequireVisionGuidance, task.Profile.RequirePickAtSource, task.Profile.RequirePlaceAtTarget);

        try
        {
            // Pre-execution device validation
            if (_options.EnableDevicePreCheck)
            {
                await ValidateDeviceReadinessAsync(task, cancellationToken);
            }

            // Phase 1: Navigate AGV to source station
            state = CompoundTaskState.NavigatingToSource;
            var navTaskId = await NavigateToStationAsync(
                task.TaskId,
                task.SourceStationId,
                cancellationToken);
            details = details with { AgvNavigationTaskId = navTaskId };
            lastKnownLocation = task.SourceStationId;

            state = CompoundTaskState.ArrivedAtSource;
            _logger.LogInformation("Task {TaskId}: AGV arrived at source station {Station}",
                task.TaskId, task.SourceStationId);

            // Phase 2: Vision guidance (if required)
            RobotArmPose pickPose = task.Profile.DefaultPickPose
                ?? throw new CompoundTaskException(
                    task.TaskId,
                    state,
                    "Default pick pose is required when pick operation is enabled");

            if (task.Profile.RequireVisionGuidance)
            {
                state = CompoundTaskState.VisionScanning;
                var visionResult = await ExecuteVisionGuidanceAsync(task, cancellationToken);

                if (!visionResult.Success)
                {
                    state = CompoundTaskState.VisionFailed;
                    throw new CompoundTaskException(
                        task.TaskId,
                        state,
                        "VISION-01",
                        $"Vision guidance failed after {visionResult.RetryCount} attempts: {visionResult.ErrorMessage}");
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
                    "Task {TaskId}: Vision completed, pick pose corrected to ({X:F1}, {Y:F1}, {Z:F1}), rotation: {Rz:F1}°",
                    task.TaskId, pickPose.X, pickPose.Y, pickPose.Z, pickPose.Rz);
            }

            // Phase 3: Robot arm pick (if required)
            if (task.Profile.RequirePickAtSource)
            {
                state = CompoundTaskState.Picking;
                var pickOpId = await ExecutePickAsync(pickPose, cancellationToken);
                details = details with { PickOperationId = pickOpId };
                isHoldingObject = true;

                state = CompoundTaskState.PickCompleted;
                _logger.LogInformation("Task {TaskId}: Pick operation completed", task.TaskId);
            }

            // Phase 4: Navigate AGV to target station
            state = CompoundTaskState.NavigatingToTarget;
            await NavigateToStationAsync(task.TaskId, task.TargetStationId, cancellationToken);
            lastKnownLocation = task.TargetStationId;

            state = CompoundTaskState.ArrivedAtTarget;
            _logger.LogInformation("Task {TaskId}: AGV arrived at target station {Station}",
                task.TaskId, task.TargetStationId);

            // Phase 5: Robot arm place (if required)
            if (task.Profile.RequirePlaceAtTarget)
            {
                state = CompoundTaskState.Placing;
                var placePose = task.Profile.DefaultPlacePose
                    ?? throw new CompoundTaskException(
                        task.TaskId,
                        state,
                        "Default place pose is required when place operation is enabled");
                var placeOpId = await ExecutePlaceAsync(placePose, cancellationToken);
                details = details with { PlaceOperationId = placeOpId };
                isHoldingObject = false;

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
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Task {TaskId} was cancelled at state {State}", task.TaskId, state);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Task {TaskId} failed at state {State}: {Error}",
                task.TaskId, state, ex.Message);

            // Attempt rollback if enabled
            if (_options.EnableRollbackOnFailure && isHoldingObject)
            {
                await AttemptRollbackAsync(task, lastKnownLocation, ex);
            }

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

    /// <summary>
    /// Validate that all required devices are ready before task execution.
    /// </summary>
    private async Task ValidateDeviceReadinessAsync(
        CompoundTransportTask task,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.DeviceStatusCheckTimeout);

        try
        {
            // Check AGV status
            var agvStatus = await _agvDriver.GetSnapshotAsync("AGV-01", timeoutCts.Token);
            if (!agvStatus.Online)
            {
                throw new CompoundTaskException(
                    task.TaskId,
                    CompoundTaskState.Created,
                    "AGV-01",
                    "AGV is offline or not responding");
            }

            _logger.LogDebug("Device check: AGV online at {Location}", agvStatus.CurrentStationId ?? "unknown");

            // Check robot arm status (if required)
            if (task.Profile.RequirePickAtSource || task.Profile.RequirePlaceAtTarget)
            {
                var armStatus = await _armDriver.GetStatusAsync(timeoutCts.Token);
                if (!armStatus.IsConnected)
                {
                    throw new CompoundTaskException(
                        task.TaskId,
                        CompoundTaskState.Created,
                        armStatus.ArmId,
                        "Robot arm is not connected");
                }
                if (!armStatus.IsEnabled)
                {
                    throw new CompoundTaskException(
                        task.TaskId,
                        CompoundTaskState.Created,
                        armStatus.ArmId,
                        "Robot arm is not enabled");
                }
                if (armStatus.HasError)
                {
                    throw new CompoundTaskException(
                        task.TaskId,
                        CompoundTaskState.Created,
                        armStatus.ArmId,
                        $"Robot arm has error: {armStatus.ErrorMessage}");
                }

                _logger.LogDebug("Device check: Robot arm ready, state={State}", armStatus.State);
            }

            // Check vision system calibration (if required)
            if (task.Profile.RequireVisionGuidance)
            {
                var calibration = await _visionDriver.GetCalibrationAsync(timeoutCts.Token);
                if (!calibration.IsCalibrated)
                {
                    throw new CompoundTaskException(
                        task.TaskId,
                        CompoundTaskState.Created,
                        calibration.VisionId,
                        "Vision system is not calibrated");
                }

                _logger.LogDebug("Device check: Vision calibrated, error={Error:F2}",
                    calibration.ReprojectionError ?? 0);
            }

            _logger.LogInformation("Task {TaskId}: All device readiness checks passed", task.TaskId);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new CompoundTaskException(
                task.TaskId,
                CompoundTaskState.Created,
                $"Device readiness check timed out after {_options.DeviceStatusCheckTimeout.TotalSeconds:F1}s");
        }
    }

    /// <summary>
    /// Attempt to rollback the task by returning held object to source location.
    /// </summary>
    private async Task AttemptRollbackAsync(
        CompoundTransportTask task,
        string? lastKnownLocation,
        Exception originalException)
    {
        _logger.LogWarning(
            "Task {TaskId}: Attempting rollback, robot arm is holding object",
            task.TaskId);

        try
        {
            // If we're not at source station, try to navigate back
            if (lastKnownLocation != task.SourceStationId)
            {
                _logger.LogInformation("Rollback: Navigating back to source station {Station}",
                    task.SourceStationId);
                await NavigateToStationAsync(
                    task.TaskId,
                    task.SourceStationId,
                    CancellationToken.None);
            }

            // Place object back at source
            var rollbackPose = task.Profile.DefaultPickPose
                ?? throw new InvalidOperationException("Cannot rollback without pick pose");

            _logger.LogInformation("Rollback: Placing object back at source");
            await _armDriver.PlaceAsync(
                new RobotArmPlaceCommand(
                    OperationId: Guid.NewGuid(),
                    TargetPose: rollbackPose),
                CancellationToken.None);

            // Return arm to home
            await _armDriver.HomeAsync(CancellationToken.None);

            _logger.LogInformation("Task {TaskId}: Rollback completed successfully", task.TaskId);
        }
        catch (Exception rollbackEx)
        {
            _logger.LogError(
                rollbackEx,
                "Task {TaskId}: Rollback failed - MANUAL INTERVENTION REQUIRED. " +
                "Robot arm may still be holding object. Original error: {OriginalError}",
                task.TaskId, originalException.Message);
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

        // Wait for arrival with progress reporting
        await WaitForAgvArrivalAsync(taskId, stationId, cancellationToken);

        return taskId;
    }

    private async Task WaitForAgvArrivalAsync(
        Guid taskId,
        string expectedStation,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(_options.AgvNavigationTimeout);
        var lastLogTime = DateTimeOffset.MinValue;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var snapshot = await _agvDriver.GetSnapshotAsync("AGV-01", cancellationToken);

            // Report progress periodically
            if (DateTimeOffset.UtcNow - lastLogTime >= _options.ProgressReportInterval)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                _logger.LogInformation(
                    "Task {TaskId}: Waiting for AGV arrival at {Target}, current location: {Current}, remaining: {Remaining:F0}s",
                    taskId, expectedStation, snapshot.CurrentStationId ?? "unknown", remaining.TotalSeconds);
                lastLogTime = DateTimeOffset.UtcNow;
            }

            if (snapshot.CurrentStationId == expectedStation)
            {
                _logger.LogDebug("AGV arrived at station {StationId}", expectedStation);
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new CompoundTaskException(
            taskId,
            CompoundTaskState.NavigatingToSource,
            "AGV-01",
            $"AGV did not arrive at station {expectedStation} within {_options.AgvNavigationTimeout.TotalMinutes:F1} minutes");
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
                await Task.Delay(1000, cancellationToken);
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
                    continue;
                }

                if (recognitionResult.Confidence < minConfidence)
                {
                    _logger.LogWarning(
                        "Task {TaskId}: Vision confidence too low: {Confidence:F2} < {Min:F2}",
                        task.TaskId, recognitionResult.Confidence, minConfidence);
                    continue;
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
                    continue;
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

        _logger.LogDebug("Executing pick at ({X:F1}, {Y:F1}, {Z:F1})", pickPose.X, pickPose.Y, pickPose.Z);
        var result = await _armDriver.PickAsync(pickCommand, cancellationToken);

        if (result.State != "Completed")
        {
            throw new CompoundTaskException(
                Guid.Empty,
                CompoundTaskState.PickFailed,
                result.ArmId,
                $"Pick operation failed: {result.ErrorMessage}");
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

        _logger.LogDebug("Executing place at ({X:F1}, {Y:F1}, {Z:F1})", placePose.X, placePose.Y, placePose.Z);
        var result = await _armDriver.PlaceAsync(placeCommand, cancellationToken);

        if (result.State != "Completed")
        {
            throw new CompoundTaskException(
                Guid.Empty,
                CompoundTaskState.PlaceFailed,
                result.ArmId,
                $"Place operation failed: {result.ErrorMessage}");
        }

        return operationId;
    }

    public void Dispose()
    {
        _executionGate?.Dispose();
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
