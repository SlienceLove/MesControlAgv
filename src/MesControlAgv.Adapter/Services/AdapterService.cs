using System.Text.Json;
using MesControlAgv.Adapter;
using MesControlAgv.Contracts;
using MesControlAgv.Adapter.Data;
using MesControlAgv.Adapter.Entities;
using MesControlAgv.Adapter.Modules;
using MesControlAgv.Domain;
using MesControlAgv.Domain.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MesControlAgv.Adapter.Services;

public sealed class AdapterService
{
    private static readonly object DispatchGatesLock = new();
    private static readonly Dictionary<Guid, DispatchGate> DispatchGates = new();
    private static readonly PhysicalAgvSessionGate DefaultPhysicalSessionGate = new();
    private readonly AdapterDbContext _database;
    private readonly IAgvDeviceClient _device;
    private readonly IAgvFleetDeviceClient? _fleet;
    private readonly MultiAgvScheduler _scheduler;
    private readonly PathPlanner _planner;
    private readonly ProfileConfiguration _profile;
    private readonly PhysicalAcceptancePreflightService? _physicalPreflight;
    private readonly AdapterRunMode _runMode;
    private readonly PhysicalAgvSessionGate _physicalSessionGate;
    private readonly ILogger<AdapterService> _logger;
    private readonly DeviceOperationPolicy? _devicePolicy;

    public AdapterService(
        AdapterDbContext database,
        IAgvDeviceClient device,
        IAgvFleetDeviceClient? fleet = null,
        MultiAgvScheduler? scheduler = null,
        PathPlanner? planner = null,
        ProfileConfiguration? profile = null,
        PhysicalAcceptancePreflightService? physicalPreflight = null,
        AdapterRunMode? runMode = null,
        PhysicalAgvSessionGate? physicalSessionGate = null,
        ILogger<AdapterService>? logger = null,
        DeviceOperationPolicy? devicePolicy = null)
    {
        _database = database;
        _device = device;
        _fleet = fleet;
        _profile = profile ?? ProfileConfiguration.Default;
        _scheduler = scheduler ?? new MultiAgvScheduler(new PathPlanner(AgvMap.FromProfile(_profile.Map)));
        _planner = planner ?? new PathPlanner(AgvMap.FromProfile(_profile.Map));
        _physicalPreflight = physicalPreflight;
        _runMode = runMode ?? AdapterRunMode.Standard;
        _physicalSessionGate = physicalSessionGate ?? DefaultPhysicalSessionGate;
        _logger = logger ?? NullLogger<AdapterService>.Instance;
        _devicePolicy = devicePolicy;
    }

    public Task<AgvTaskResponse> DispatchAsync(Guid taskId, string targetStationId, CancellationToken cancellationToken) =>
        DispatchWithPhysicalSessionGateAsync(
            taskId, null, targetStationId, null, null, DispatchPermission.Standard, cancellationToken);

    public Task<AgvTaskResponse> DispatchAsync(
        Guid taskId,
        string? sourceStationId,
        string targetStationId,
        CancellationToken cancellationToken) =>
        DispatchWithPhysicalSessionGateAsync(
            taskId, sourceStationId, targetStationId, null, null, DispatchPermission.Standard, cancellationToken);

    public Task<AgvTaskResponse> DispatchAsync(
        Guid taskId,
        string? sourceStationId,
        string targetStationId,
        string? requestedAgvId,
        IReadOnlyList<string>? requestedPath,
        CancellationToken cancellationToken) =>
        DispatchWithPhysicalSessionGateAsync(
            taskId, sourceStationId, targetStationId, requestedAgvId, requestedPath, DispatchPermission.Standard, cancellationToken);

    /// <summary>
    /// Writes one AGV digital output inside the same physical-session gate used
    /// by navigation and control release. The driver owns vendor framing;
    /// this service owns device-policy and control-ownership admission.
    /// </summary>
    public Task<AgvDoWriteResponse> SetDoAsync(
        string agvId,
        int id,
        bool status,
        CancellationToken cancellationToken) =>
        _physicalSessionGate.RunAsync(async () =>
        {
            EnsureMutationIsAllowed("AGV DO write");
            EnsureDeviceControlEnabled(agvId);
            if (_device is not IAgvIoDeviceClient io)
            {
                throw new NotSupportedException("The configured AGV driver does not expose digital I/O.");
            }

            await _device.EnsureControlAsync(cancellationToken);
            return await io.SetDoAsync(id, status, cancellationToken);
        }, cancellationToken);

    public async Task<AgvTaskResponse> DispatchFieldNavigationAcceptanceAsync(
        Guid acceptanceId,
        FieldNavigationDispatchCommand command,
        CancellationToken cancellationToken)
        => await _physicalSessionGate.RunAsync(
            () => DispatchFieldNavigationAcceptanceCoreAsync(acceptanceId, command, cancellationToken),
            cancellationToken);

    /// <summary>
    /// Serializes an operator-requested release with physical dispatches so a
    /// 4006 cannot land between the final 1060 ownership check and 3066.
    /// </summary>
    public async Task<bool> ReleaseControlAsync(CancellationToken cancellationToken)
    {
        EnsureMutationIsAllowed("control release");
        EnsureDeviceControlEnabled(GetDefaultAgvId());
        return await _physicalSessionGate.RunAsync(
            // Once admitted, finish the ownership transaction with the device's
            // own bounded request timeout even if the HTTP caller disconnects.
            () => _device.ReleaseControlAsync(CancellationToken.None),
            cancellationToken);
    }

    private async Task<AgvTaskResponse> DispatchWithPhysicalSessionGateAsync(
        Guid taskId,
        string? sourceStationId,
        string targetStationId,
        string? requestedAgvId,
        IReadOnlyList<string>? requestedPath,
        DispatchPermission dispatchPermission,
        CancellationToken cancellationToken)
    {
        if (_profile.PhysicalAcceptance is null)
        {
            return await DispatchCoreAsync(
                taskId,
                sourceStationId,
                targetStationId,
                requestedAgvId,
                requestedPath,
                dispatchPermission,
                cancellationToken);
        }

        return await _physicalSessionGate.RunAsync(
            () => DispatchCoreAsync(
                taskId,
                sourceStationId,
                targetStationId,
                requestedAgvId,
                requestedPath,
                dispatchPermission,
                cancellationToken),
            cancellationToken);
    }

    private async Task<AgvTaskResponse> DispatchFieldNavigationAcceptanceCoreAsync(
        Guid acceptanceId,
        FieldNavigationDispatchCommand command,
        CancellationToken cancellationToken)
    {
        EnsureMutationIsAllowed("field-navigation dispatch");
        ArgumentNullException.ThrowIfNull(command);
        EnsureDeviceControlEnabled(command.AgvId);
        if (_profile.PhysicalAcceptance is null)
            throw new InvalidOperationException("Field navigation acceptance requires a physical acceptance profile.");
        if (!_profile.Features.EnableFieldNavigationAcceptance)
            throw new DispatchDisabledException("Field navigation acceptance is disabled by the active profile.");
        if (!_profile.Agvs.Any(agv => agv.Enabled && StringComparer.Ordinal.Equals(agv.AgvId, command.AgvId)))
            throw new KeyNotFoundException($"AGV {command.AgvId} is not enabled by the active profile.");
        if (command.PlannedPath.Count < 2
            || !StringComparer.Ordinal.Equals(command.PlannedPath[0], command.SourceStationId))
            throw new InvalidOperationException("A field navigation path must start at the approved source station.");

        var preflight = _physicalPreflight
            ?? throw new InvalidOperationException("Physical navigation preflight is not configured.");
        var beforeControl = await preflight.GetBeforeControlForFieldNavigationAcceptanceAsync(cancellationToken);
        ValidateFieldNavigationAssessment(beforeControl, command);

        var controlAcquiredByThisSession = await EnsureControlForPhysicalSessionAsync(cancellationToken);

        try
        {
            var afterControl = await preflight.GetForFieldNavigationAcceptanceAsync(cancellationToken);
            ValidateFieldNavigationAssessment(afterControl, command);
        }
        catch (Exception)
        {
            await ReleaseControlAcquiredBySessionAsync(
                controlAcquiredByThisSession,
                "AGV control release failed after post-control physical preflight rejection.");

            throw;
        }

        try
        {
            return await DispatchCoreAsync(
                acceptanceId,
                command.SourceStationId,
                command.TargetStationId,
                command.AgvId,
                command.PlannedPath,
                DispatchPermission.FieldNavigationAcceptance,
                cancellationToken);
        }
        catch (Exception)
        {
            if (controlAcquiredByThisSession
                && _device is INavigationAttemptState attemptState
                && !attemptState.MayHaveWrittenNavigation(acceptanceId))
            {
                await ReleaseControlAcquiredBySessionAsync(
                    true,
                    "AGV control release failed after field navigation stopped before a navigation write.");
            }

            throw;
        }
    }

    private async Task<bool> EnsureControlForPhysicalSessionAsync(CancellationToken cancellationToken)
    {
        if (_device is IControlAcquisitionEvidence evidence)
        {
            var result = await evidence.EnsureControlWithResultAsync(cancellationToken);
            return result.AcquiredByThisCall;
        }

        // A legacy physical client cannot demonstrate whether it inherited
        // ownership. It may dispatch, but this session must never auto-release.
        await _device.EnsureControlAsync(cancellationToken);
        return false;
    }

    private async Task ReleaseControlAcquiredBySessionAsync(bool controlAcquiredByThisSession, string failureMessage)
    {
        if (!controlAcquiredByThisSession) return;

        try
        {
            await _device.ReleaseControlAsync(CancellationToken.None);
        }
        catch (Exception releaseException)
        {
            _logger.LogError(releaseException, failureMessage);
        }
    }

    private static void ValidateFieldNavigationAssessment(
        PhysicalAgvPreflightResponse assessment,
        FieldNavigationDispatchCommand command)
    {
        if (!assessment.DispatchPermitted)
            throw new PhysicalPreflightRejectedException(assessment.BlockingReasons);
        if (!StringComparer.Ordinal.Equals(assessment.Snapshot.AgvId, command.AgvId))
            throw new AgvUnavailableException($"Preflight returned AGV {assessment.Snapshot.AgvId}, not {command.AgvId}.");
        if (!StringComparer.Ordinal.Equals(assessment.Snapshot.CurrentStationId, command.SourceStationId))
            throw new AgvUnavailableException(
                $"Preflight location is {assessment.Snapshot.CurrentStationId ?? "unknown"}, not {command.SourceStationId}.");
    }

    private async Task<AgvTaskResponse> DispatchCoreAsync(
        Guid taskId,
        string? sourceStationId,
        string targetStationId,
        string? requestedAgvId,
        IReadOnlyList<string>? requestedPath,
        DispatchPermission dispatchPermission,
        CancellationToken cancellationToken)
    {
        EnsureMutationIsAllowed("task dispatch");
        var gate = AcquireDispatchGate(taskId);
        var acquired = false;
        var waited = false;
        var controlAcquiredByThisSession = false;
        try
        {
            if (gate.Semaphore.Wait(0))
            {
                acquired = true;
            }
            else
            {
                waited = true;
                await gate.Semaphore.WaitAsync(cancellationToken);
                acquired = true;
            }

            var existing = await _database.Tasks.FindAsync([taskId], cancellationToken);
            if (existing is not null && (existing.State != "failed" || waited))
            {
                var existingSnapshot = await GetSnapshotAsync(existing.AgvId, cancellationToken);
                if (!existingSnapshot.Online || existingSnapshot.ControlOwner != "adapter")
                {
                    throw new ControlUnavailableException(existingSnapshot.ControlOwner);
                }
                if (existing.State is "unknown" or "dispatching")
                {
                    var existingPath = DeserializePath(existing.PathJson);
                    var reconciled = await GetTaskFromDeviceAsync(existing.AgvId, taskId, existingPath, cancellationToken);
                    if (reconciled is not null)
                    {
                        existing.State = reconciled.State;
                        existing.DeviceTaskId = reconciled.DeviceTaskId;
                        existing.LastError = reconciled.LastError;
                        await _database.SaveChangesAsync(cancellationToken);
                    }
                    else if (existing.State == "dispatching")
                    {
                        existing.State = "unknown";
                        existing.LastError = "dispatch_not_confirmed_by_1110";
                        await _database.SaveChangesAsync(cancellationToken);
                    }
                }
                return ToResponse(existing);
            }

            if (dispatchPermission == DispatchPermission.Standard && !_profile.Features.EnableAutomaticDispatch)
            {
                throw new DispatchDisabledException();
            }
            if (dispatchPermission == DispatchPermission.FieldNavigationAcceptance
                && !_profile.Features.EnableFieldNavigationAcceptance)
            {
                throw new DispatchDisabledException("Field navigation acceptance is disabled by the active profile.");
            }

            var validatedRequestedPath = requestedPath is null
                ? null
                : ValidateRequestedPath(sourceStationId, targetStationId, requestedPath).Stations;
            EnsurePotentialAgvControlEnabled(requestedAgvId);
            if (dispatchPermission != DispatchPermission.FieldNavigationAcceptance)
            {
                if (_profile.PhysicalAcceptance is null)
                {
                    await _device.EnsureControlAsync(cancellationToken);
                }
                else
                {
                    controlAcquiredByThisSession = await EnsureControlForPhysicalSessionAsync(cancellationToken);
                }
            }
            var assignment = await SelectAgvAsync(
                taskId,
                sourceStationId,
                targetStationId,
                requestedAgvId,
                cancellationToken);
            EnsureDeviceControlEnabled(assignment.AgvId);
            var snapshot = await GetSnapshotAsync(assignment.AgvId, cancellationToken);
            if (!snapshot.Online || snapshot.ControlOwner != "adapter") throw new ControlUnavailableException(snapshot.ControlOwner);
            if (snapshot.CurrentTaskId is { } activeTaskId && activeTaskId != taskId)
            {
                throw new AgvUnavailableException($"AGV {assignment.AgvId} already has active task {activeTaskId:N}.");
            }
            if (string.IsNullOrWhiteSpace(snapshot.CurrentStationId))
            {
                throw new AgvUnavailableException($"AGV {assignment.AgvId} current station is unknown.");
            }

            var path = validatedRequestedPath ?? assignment.Path?.Stations;
            if (path is { Count: > 0 }
                && snapshot.CurrentStationId is { } currentStation
                && !StringComparer.Ordinal.Equals(path[0], currentStation))
            {
                throw new AgvUnavailableException(
                    $"The planned path starts at {path[0]}, but AGV {assignment.AgvId} is currently at {currentStation}.");
            }

            var task = existing ?? new AdapterTask { TaskId = taskId };
            task.AgvId = assignment.AgvId;
            task.DeviceTaskId = taskId.ToString("N");
            task.TargetStationId = targetStationId;
            task.State = "dispatching";
            task.LastError = null;
            task.PathJson = path is null ? null : JsonSerializer.Serialize(path);
            if (existing is null) _database.Tasks.Add(task);
            await _database.SaveChangesAsync(cancellationToken);

            var navigationSourceStationId = path is { Count: > 0 }
                ? path[0]
                : sourceStationId;
            AgvTaskResponse response;
            try
            {
                response = _fleet is not null
                    ? await _fleet.NavigateAsync(assignment.AgvId, taskId, navigationSourceStationId, targetStationId, path, cancellationToken)
                    : await _device.NavigateAsync(taskId, navigationSourceStationId, targetStationId, path, cancellationToken);
                response = response with { AgvId = assignment.AgvId, Path = path };
            }
            catch (TimeoutException) when (
                _profile.PhysicalAcceptance is null
                || _device is not INavigationAttemptState navigationAttempt
                || navigationAttempt.MayHaveWrittenNavigation(taskId))
            {
                response = await GetTaskFromDeviceAsync(assignment.AgvId, taskId, path, cancellationToken)
                    ?? new AgvTaskResponse(taskId, taskId.ToString("N"), targetStationId, "unknown", "timeout", assignment.AgvId, path);
            }

            task.DeviceTaskId = response.DeviceTaskId;
            task.State = response.State;
            task.LastError = response.LastError;
            await _database.SaveChangesAsync(cancellationToken);
            return ToResponse(task);
        }
        catch (Exception)
        {
            if (controlAcquiredByThisSession
                && _fleet is null
                && _device is INavigationAttemptState attemptState
                && !attemptState.MayHaveWrittenNavigation(taskId))
            {
                await ReleaseControlAcquiredBySessionAsync(
                    true,
                    "AGV control release failed after physical dispatch stopped before a navigation write.");
            }

            throw;
        }
        finally
        {
            if (acquired) gate.Semaphore.Release();
            ReleaseDispatchGate(taskId, gate);
        }
    }

    public async Task<AgvTaskResponse?> GetAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await _database.Tasks.FindAsync([taskId], cancellationToken);
        if (task is null) return null;

        var path = DeserializePath(task.PathJson);
        var deviceTask = await GetTaskFromDeviceAsync(task.AgvId, taskId, path, cancellationToken);
        if (deviceTask is not null)
        {
            task.State = deviceTask.State;
            task.DeviceTaskId = deviceTask.DeviceTaskId;
            task.LastError = deviceTask.LastError;
            await _database.SaveChangesAsync(cancellationToken);
            ReleaseCompletedRoute(taskId, deviceTask.State);
        }
        else if (task.State is "dispatching" or "accepted" or "moving" or "paused")
        {
            task.State = "unknown";
            task.LastError = "dispatch_not_confirmed_by_1110";
            await _database.SaveChangesAsync(cancellationToken);
        }

        return ToResponse(task);
    }

    public async Task<IReadOnlyList<AgvSnapshotResponse>> GetFleetAsync(CancellationToken cancellationToken)
    {
        var snapshots = _fleet is not null
            ? await _fleet.GetFleetSnapshotAsync(cancellationToken)
            : [await _device.GetSnapshotAsync(cancellationToken)];
        return snapshots
            .Select(snapshot => snapshot with { Capabilities = snapshot.Capabilities ?? AgvCapabilitiesResponse.Standard })
            .ToList();
    }

    public async Task<AgvTaskResponse?> ExecuteCommandAsync(
        string agvId,
        string command,
        Guid? requestedTaskId,
        CancellationToken cancellationToken)
    {
        EnsureMutationIsAllowed("AGV command");
        EnsureDeviceControlEnabled(agvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var normalizedCommand = command.Trim().ToLowerInvariant();
        if (normalizedCommand is not ("pause" or "stop" or "resume" or "continue" or "cancel"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                command,
                "Supported commands: pause, resume, cancel.");
        }
        if (normalizedCommand == "cancel" && !_profile.Features.EnableTaskCancellation)
        {
            throw new DispatchDisabledException("Task cancellation is disabled by the active profile.");
        }
        if (normalizedCommand is "pause" or "stop")
        {
            EnsurePhysicalLifecycleAllowed("pause");
        }
        else if (normalizedCommand is "resume" or "continue")
        {
            EnsurePhysicalLifecycleAllowed("resume");
        }

        if (requestedTaskId is { } requestedId)
        {
            return await RunPhysicalWriteSessionAsync(
                () => normalizedCommand switch
                {
                    "pause" or "stop" => PauseCoreAsync(requestedId, cancellationToken, agvId),
                    "resume" or "continue" => ResumeCoreAsync(requestedId, cancellationToken, agvId),
                    "cancel" => CancelCoreAsync(requestedId, cancellationToken, agvId),
                    _ => throw new InvalidOperationException("The AGV command was not normalized to a supported operation.")
                },
                cancellationToken);
        }

        if (normalizedCommand == "cancel")
        {
            return await RunPhysicalWriteSessionAsync(
                () => ExecuteCancelCommandCoreAsync(agvId, cancellationToken),
                cancellationToken);
        }

        var snapshots = await GetFleetAsync(cancellationToken);
        var snapshot = snapshots.SingleOrDefault(item => StringComparer.Ordinal.Equals(item.AgvId, agvId))
            ?? throw new KeyNotFoundException($"AGV {agvId} is not configured.");
        var taskId = snapshot.CurrentTaskId
            ?? throw new InvalidOperationException($"AGV {agvId} has no active task.");

        return normalizedCommand switch
        {
            "pause" or "stop" => await PauseCoreAsync(taskId, cancellationToken, agvId),
            "resume" or "continue" => await ResumeCoreAsync(taskId, cancellationToken, agvId),
            _ => throw new InvalidOperationException("The AGV command was not normalized to a supported operation.")
        };
    }

    private async Task<AgvTaskResponse?> ExecuteCancelCommandCoreAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        var snapshots = await GetFleetAsync(cancellationToken);
        var snapshot = snapshots.SingleOrDefault(item => StringComparer.Ordinal.Equals(item.AgvId, agvId))
            ?? throw new KeyNotFoundException($"AGV {agvId} is not configured.");
        var taskId = snapshot.CurrentTaskId
            ?? throw new InvalidOperationException($"AGV {agvId} has no active task.");
        return await CancelCoreAsync(taskId, cancellationToken, agvId);
    }

    public async Task<AgvTaskResponse?> PauseAsync(Guid taskId, CancellationToken cancellationToken)
    {
        EnsureMutationIsAllowed("pause");
        EnsurePhysicalLifecycleAllowed("pause");
        return await RunPhysicalWriteSessionAsync(
            () => PauseCoreAsync(taskId, cancellationToken),
            cancellationToken);
    }

    private async Task<AgvTaskResponse?> PauseCoreAsync(
        Guid taskId,
        CancellationToken cancellationToken,
        string? expectedAgvId = null)
    {
        EnsureMutationIsAllowed("pause");
        var task = await _database.Tasks.FindAsync([taskId], cancellationToken);
        if (task is null) return null;
        EnsureTaskBelongsToAgv(task, expectedAgvId);
        EnsureDeviceControlEnabled(task.AgvId);
        var path = DeserializePath(task.PathJson);
        await _device.EnsureControlAsync(cancellationToken);
        var deviceTask = _fleet is not null
            ? await _fleet.PauseAsync(task.AgvId, taskId, path, cancellationToken)
            : await _device.PauseAsync(taskId, path, cancellationToken);
        return await PersistDeviceStateAsync(taskId, deviceTask, "paused", cancellationToken);
    }

    public async Task<AgvTaskResponse?> ResumeAsync(Guid taskId, CancellationToken cancellationToken)
    {
        EnsureMutationIsAllowed("resume");
        EnsurePhysicalLifecycleAllowed("resume");
        return await RunPhysicalWriteSessionAsync(
            () => ResumeCoreAsync(taskId, cancellationToken),
            cancellationToken);
    }

    private async Task<AgvTaskResponse?> ResumeCoreAsync(
        Guid taskId,
        CancellationToken cancellationToken,
        string? expectedAgvId = null)
    {
        EnsureMutationIsAllowed("resume");
        var task = await _database.Tasks.FindAsync([taskId], cancellationToken);
        if (task is null) return null;
        EnsureTaskBelongsToAgv(task, expectedAgvId);
        EnsureDeviceControlEnabled(task.AgvId);
        var path = DeserializePath(task.PathJson);
        await _device.EnsureControlAsync(cancellationToken);
        var deviceTask = _fleet is not null
            ? await _fleet.ResumeAsync(task.AgvId, taskId, path, cancellationToken)
            : await _device.ResumeAsync(taskId, path, cancellationToken);
        return await PersistDeviceStateAsync(taskId, deviceTask, "moving", cancellationToken);
    }

    public async Task<AgvTaskResponse?> CancelAsync(Guid taskId, CancellationToken cancellationToken)
    {
        EnsureMutationIsAllowed("cancellation");
        if (!_profile.Features.EnableTaskCancellation)
        {
            throw new DispatchDisabledException("Task cancellation is disabled by the active profile.");
        }

        return await RunPhysicalWriteSessionAsync(
            () => CancelCoreAsync(taskId, cancellationToken),
            cancellationToken);
    }

    private async Task<AgvTaskResponse?> CancelCoreAsync(
        Guid taskId,
        CancellationToken cancellationToken,
        string? expectedAgvId = null)
    {
        EnsureMutationIsAllowed("cancellation");
        var task = await _database.Tasks.FindAsync([taskId], cancellationToken);
        if (task is null) return null;
        EnsureTaskBelongsToAgv(task, expectedAgvId);
        EnsureDeviceControlEnabled(task.AgvId);

        var controlAcquiredByThisSession = false;
        try
        {
            if (_profile.PhysicalAcceptance is null)
            {
                await _device.EnsureControlAsync(cancellationToken);
            }
            else
            {
                controlAcquiredByThisSession = await EnsureControlForPhysicalSessionAsync(cancellationToken);
            }

            var snapshot = await GetSnapshotAsync(task.AgvId, cancellationToken);
            if (!snapshot.Online || snapshot.ControlOwner != "adapter") throw new ControlUnavailableException(snapshot.ControlOwner);

            var path = DeserializePath(task.PathJson);
            var deviceTask = _fleet is not null
                ? await _fleet.CancelAsync(task.AgvId, taskId, path, cancellationToken)
                : await _device.CancelAsync(taskId, path, cancellationToken);
            if (deviceTask is { State: "unknown" })
            {
                task.State = deviceTask.State;
                task.DeviceTaskId = deviceTask.DeviceTaskId;
                task.LastError = deviceTask.LastError ?? "cancel_not_confirmed_by_1110";
                await _database.SaveChangesAsync(cancellationToken);
                return ToResponse(task);
            }
            if (deviceTask is not { State: "cancelled" })
                throw new InvalidOperationException("Device did not confirm cancellation.");

            task.State = deviceTask.State;
            task.DeviceTaskId = deviceTask.DeviceTaskId;
            task.LastError = deviceTask.LastError;
            await _database.SaveChangesAsync(cancellationToken);
            _scheduler.Release(taskId);
            return ToResponse(task);
        }
        catch (Exception)
        {
            if (controlAcquiredByThisSession
                && _fleet is null
                && _device is ICancellationAttemptState attemptState
                && !attemptState.MayHaveWrittenCancellation(taskId))
            {
                await ReleaseControlAcquiredBySessionAsync(
                    true,
                    "AGV control release failed after physical cancellation stopped before a cancellation write.");
            }

            throw;
        }
    }

    private async Task<AgvTaskResponse?> PersistDeviceStateAsync(
        Guid taskId,
        AgvTaskResponse? deviceTask,
        string fallbackState,
        CancellationToken cancellationToken)
    {
        var task = await _database.Tasks.FindAsync([taskId], cancellationToken);
        if (task is null) return null;
        task.State = deviceTask?.State ?? fallbackState;
        task.DeviceTaskId = deviceTask?.DeviceTaskId ?? task.DeviceTaskId;
        task.LastError = deviceTask?.LastError;
        await _database.SaveChangesAsync(cancellationToken);
        return ToResponse(task);
    }

    private void EnsureMutationIsAllowed(string operation) => _runMode.ThrowIfMutationIsBlocked(operation);

    private void EnsureDeviceControlEnabled(string agvId) =>
        _devicePolicy?.EnsureControlEnabled(agvId);

    private void EnsurePotentialAgvControlEnabled(string? requestedAgvId)
    {
        if (_devicePolicy is null) return;
        if (!string.IsNullOrWhiteSpace(requestedAgvId))
        {
            EnsureDeviceControlEnabled(requestedAgvId);
            return;
        }

        foreach (var agv in _profile.Agvs.Where(agv => agv.Enabled))
        {
            EnsureDeviceControlEnabled(agv.AgvId);
        }
    }

    private string GetDefaultAgvId() =>
        _profile.Agvs.FirstOrDefault(agv => agv.Enabled)?.AgvId
        ?? _profile.Agvs.First().AgvId;

    private static void EnsureTaskBelongsToAgv(AdapterTask task, string? expectedAgvId)
    {
        if (expectedAgvId is not null && !StringComparer.Ordinal.Equals(task.AgvId, expectedAgvId))
        {
            throw new AgvUnavailableException(
                $"Task {task.TaskId:N} belongs to AGV {task.AgvId}, not {expectedAgvId}.");
        }
    }

    private void EnsurePhysicalLifecycleAllowed(string operation)
    {
        if (_profile.PhysicalAcceptance is not null)
        {
            throw new DispatchDisabledException(
                $"Physical acceptance mode rejects {operation} until an explicit lifecycle authorization is configured.");
        }
    }

    private Task<T> RunPhysicalWriteSessionAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return _profile.PhysicalAcceptance is null
            ? operation()
            : _physicalSessionGate.RunAsync(operation, cancellationToken);
    }

    private async Task<(string AgvId, PlannedPath? Path)> SelectAgvAsync(
        Guid taskId,
        string? sourceStationId,
        string targetStationId,
        string? requestedAgvId,
        CancellationToken cancellationToken)
    {
        if (_fleet is null)
        {
            var snapshot = await _device.GetSnapshotAsync(cancellationToken);
            if (!snapshot.Online || snapshot.ControlOwner != "adapter")
            {
                throw new ControlUnavailableException(snapshot.ControlOwner);
            }
            if (string.IsNullOrWhiteSpace(snapshot.CurrentStationId))
            {
                throw new AgvUnavailableException($"AGV {snapshot.AgvId} current station is unknown.");
            }

            var currentStationId = snapshot.CurrentStationId;
            var path = _planner.PlanVia(
                currentStationId,
                sourceStationId ?? currentStationId,
                targetStationId);
            return (requestedAgvId ?? snapshot.AgvId, path);
        }

        var snapshots = await _fleet.GetFleetSnapshotAsync(cancellationToken);
        _scheduler.ReleaseForIdleAgvs(snapshots.Where(snapshot => snapshot.CurrentTaskId is null)
            .Select(snapshot => snapshot.AgvId)
            .ToHashSet(StringComparer.Ordinal));

        var candidates = snapshots
            .Where(snapshot => (requestedAgvId is null || StringComparer.Ordinal.Equals(snapshot.AgvId, requestedAgvId))
                && !string.IsNullOrWhiteSpace(snapshot.CurrentStationId))
            .Select(snapshot => new AgvCandidate(
                snapshot.AgvId,
                snapshot.Online,
                snapshot.ControlOwner,
                snapshot.CurrentStationId!,
                snapshot.CurrentTaskId is not null))
            .ToArray();
        var decision = _scheduler.Schedule(
            taskId,
            sourceStationId ?? HomeStationId,
            targetStationId,
            candidates);
        if (!decision.Assigned || decision.AgvId is null)
        {
            throw new AgvUnavailableException(decision.Reason ?? "No AGV can accept the route.");
        }

        return (decision.AgvId, decision.Path);
    }

    private PlannedPath ValidateRequestedPath(
        string? sourceStationId,
        string targetStationId,
        IReadOnlyList<string> requestedPath)
    {
        var path = _planner.ValidatePath(requestedPath);
        if (!StringComparer.Ordinal.Equals(path.End, targetStationId.Trim()))
        {
            throw new InvalidOperationException("The final path station must match the navigation target.");
        }

        if (sourceStationId is { Length: > 0 }
            && !path.Stations.Contains(sourceStationId.Trim(), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The planned path must include the navigation source station.");
        }

        return path;
    }

    private string HomeStationId => _profile.Agvs.FirstOrDefault(agv => agv.Enabled)?.HomeStationId
        ?? _profile.Map.StationIds.FirstOrDefault()
        ?? throw new InvalidOperationException("The active AGV profile does not declare a home station.");

    private async Task<AgvSnapshotResponse> GetSnapshotAsync(string agvId, CancellationToken cancellationToken)
    {
        if (_fleet is not null)
        {
            var snapshot = (await _fleet.GetFleetSnapshotAsync(cancellationToken))
                .SingleOrDefault(item => StringComparer.Ordinal.Equals(item.AgvId, agvId));
            return snapshot ?? throw new KeyNotFoundException($"AGV {agvId} was not found.");
        }

        return await _device.GetSnapshotAsync(cancellationToken);
    }

    private async Task<AgvTaskResponse?> GetTaskFromDeviceAsync(
        string agvId,
        Guid taskId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken) =>
        _fleet is not null
            ? await _fleet.GetTaskAsync(agvId, taskId, path, cancellationToken)
            : await _device.GetTaskAsync(taskId, path, cancellationToken);

    private void ReleaseCompletedRoute(Guid taskId, string state)
    {
        if (state is "arrived" or "completed" or "cancelled" or "failed") _scheduler.Release(taskId);
    }

    private static DispatchGate AcquireDispatchGate(Guid taskId)
    {
        lock (DispatchGatesLock)
        {
            if (!DispatchGates.TryGetValue(taskId, out var gate))
            {
                gate = new DispatchGate();
                DispatchGates.Add(taskId, gate);
            }
            gate.References++;
            return gate;
        }
    }

    private static void ReleaseDispatchGate(Guid taskId, DispatchGate gate)
    {
        lock (DispatchGatesLock)
        {
            gate.References--;
            if (gate.References == 0) DispatchGates.Remove(taskId);
        }
    }

    private sealed class DispatchGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    private enum DispatchPermission
    {
        Standard,
        FieldNavigationAcceptance
    }

    private static AgvTaskResponse ToResponse(AdapterTask task) => new(
        task.TaskId,
        task.DeviceTaskId,
        task.TargetStationId,
        task.State,
        task.LastError,
        task.AgvId,
        DeserializePath(task.PathJson));

    private static IReadOnlyList<string>? DeserializePath(string? pathJson) =>
        pathJson is null ? null : JsonSerializer.Deserialize<IReadOnlyList<string>>(pathJson);
}

public sealed class AgvUnavailableException(string reason)
    : InvalidOperationException(reason);

public sealed class DispatchDisabledException(string? message = null)
    : InvalidOperationException(message ?? "Automatic AGV dispatch is disabled by the active profile.");
