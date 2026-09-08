using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Mes.Services;

/// <summary>
/// Read-only AGV projection.  Every invoked gateway method is a query; this
/// probe has no dispatch, control-acquisition, cancellation, or I/O path.
/// </summary>
public sealed class PhysicalAgvReadinessProbe(
    IAgvGateway gateway,
    ProfileConfiguration profile,
    TimeProvider timeProvider) : IPhysicalDeviceReadinessProbe
{
    private IReadOnlyList<AgvSnapshotResponse>? _fleetSnapshot;

    public bool CanProbe(PhysicalDeviceDescriptor device) =>
        string.Equals(
            device.DeviceFamily,
            WorkflowDeviceFamilyIds.Agv,
            StringComparison.OrdinalIgnoreCase);

    public async Task<PhysicalDeviceReadinessObservation> ProbeAsync(
        PhysicalDeviceDescriptor device,
        bool fullPreflight,
        CancellationToken cancellationToken)
    {
        var snapshot = await ReadSnapshotAsync(device.DeviceId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (snapshot is null)
        {
            return new PhysicalDeviceReadinessObservation
            {
                DeviceId = device.DeviceId,
                DeviceFamily = device.DeviceFamily,
                ProbeSucceeded = true,
                Online = false,
                IsFullPreflight = fullPreflight,
                FullPreflightPassed = false,
                BlockingReasons = ["agv_not_returned_by_gateway"],
                FullPreflightBlockingReasons = fullPreflight
                    ? ["agv_not_returned_by_gateway"]
                    : [],
                ObservedAtUtc = now,
                FullPreflightObservedAtUtc = fullPreflight ? now : null
            };
        }

        AgvTaskResponse? activeTask = null;
        string? activeTaskReadError = null;
        if (snapshot.CurrentTaskId is { } activeTaskId)
        {
            try
            {
                activeTask = await gateway.GetTaskAsync(activeTaskId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                HttpRequestException or
                TaskCanceledException or
                TimeoutException or
                InvalidOperationException)
            {
                activeTaskReadError = exception.Message;
            }
        }

        var currentReasons = GetSnapshotBlockingReasons(snapshot, activeTaskReadError).ToList();
        if (!fullPreflight)
        {
            return FromAgv(
                device,
                snapshot,
                activeTask,
                currentReasons,
                isFullPreflight: false,
                fullPreflightPassed: false,
                mapEvidence: null,
                observedAtUtc: now,
                activeTaskReadError);
        }

        if (gateway is not IPhysicalPreflightAgvGateway physical)
        {
            AddReason(currentReasons, "physical_preflight_not_supported_by_gateway");
            return FromAgv(
                device,
                snapshot,
                activeTask,
                currentReasons,
                isFullPreflight: true,
                fullPreflightPassed: false,
                mapEvidence: null,
                observedAtUtc: now,
                activeTaskReadError);
        }

        var assessment = gateway is IPhysicalPreflightAgvDeviceGateway perDevice
            ? await perDevice.GetPhysicalPreflightAsync(device.DeviceId, cancellationToken)
            : await physical.GetPhysicalPreflightAsync(cancellationToken);
        if (!string.Equals(
                assessment.Snapshot.AgvId,
                device.DeviceId,
                StringComparison.OrdinalIgnoreCase))
        {
            AddReason(currentReasons, "physical_preflight_device_identity_mismatch");
            return FromAgv(
                device,
                snapshot,
                activeTask,
                currentReasons,
                isFullPreflight: true,
                fullPreflightPassed: false,
                mapEvidence: assessment.MapEvidence,
                observedAtUtc: now,
                activeTaskReadError);
        }

        var assessedSnapshot = assessment.Snapshot with
        {
            SafetyReadiness = assessment.Readiness ?? assessment.Snapshot.SafetyReadiness
        };
        var assessmentReasons = assessment.BlockingReasons
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Select(NormalizePreflightReason)
            .ToArray();
        var expectedPolicyBlockerObserved = assessmentReasons.Any(IsExpectedPolicyBlocker);
        var reasons = assessmentReasons
            .Where(reason => !IsExpectedPolicyBlocker(reason))
            .Concat(GetSnapshotBlockingReasons(assessedSnapshot, activeTaskReadError))
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (assessment.Readiness is null)
            AddReason(reasons, "agv_safety_readiness_unavailable");
        if (assessment.MapEvidence is null)
            AddReason(reasons, "controller_map_evidence_unavailable");
        if (!assessment.DispatchPermitted &&
            !expectedPolicyBlockerObserved &&
            reasons.Count == 0)
        {
            AddReason(reasons, "physical_preflight_rejected_without_device_reason");
        }
        return FromAgv(
            device,
            assessedSnapshot,
            activeTask,
            reasons,
            isFullPreflight: true,
            // The supervisor describes device readiness, not whether a write
            // feature was enabled for this process.  A safe pre-control AGV is
            // allowed to be idle with no Adapter ownership while the separate
            // workflow/acceptance gates keep dispatch disabled.
            fullPreflightPassed: reasons.Count == 0,
            assessment.MapEvidence,
            observedAtUtc: now,
            activeTaskReadError);
    }

    private async Task<AgvSnapshotResponse?> ReadSnapshotAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (_fleetSnapshot is null)
        {
            _fleetSnapshot = gateway is IFleetAwareAgvGateway fleet
                ? await fleet.GetFleetSnapshotAsync(cancellationToken)
                : [await gateway.GetSnapshotAsync(cancellationToken)];
        }

        return _fleetSnapshot.FirstOrDefault(snapshot =>
            string.Equals(snapshot.AgvId, deviceId, StringComparison.OrdinalIgnoreCase));
    }

    private PhysicalDeviceReadinessObservation FromAgv(
        PhysicalDeviceDescriptor descriptor,
        AgvSnapshotResponse snapshot,
        AgvTaskResponse? activeTask,
        IReadOnlyList<string> reasons,
        bool isFullPreflight,
        bool fullPreflightPassed,
        ControllerMapEvidenceResponse? mapEvidence,
        DateTimeOffset observedAtUtc,
        string? activeTaskReadError)
    {
        var readiness = snapshot.SafetyReadiness;
        return new PhysicalDeviceReadinessObservation
        {
            DeviceId = descriptor.DeviceId,
            DeviceFamily = descriptor.DeviceFamily,
            ProbeSucceeded = true,
            Online = snapshot.Online,
            CurrentStationId = snapshot.CurrentStationId,
            ActiveTaskId = snapshot.CurrentTaskId,
            ActiveTaskState = activeTask?.State,
            ActiveDeviceTaskId = activeTask?.DeviceTaskId,
            ActiveTaskTargetStationId = activeTask?.TargetStationId,
            ActiveTaskError = activeTask?.LastError ?? activeTaskReadError,
            ControlOwner = snapshot.ControlOwner,
            MapName = mapEvidence?.MapName ?? readiness?.MapName,
            MapVersion = mapEvidence?.Version,
            MapMd5 = mapEvidence?.Md5 ?? readiness?.MapMd5,
            LocalizationConfidence = readiness?.LocalizationConfidence,
            Emergency = readiness?.Emergency,
            Blocked = readiness?.Blocked,
            FatalCount = readiness?.FatalCount,
            ErrorCount = readiness?.ErrorCount,
            RelocationStatus = readiness?.RelocationStatus,
            VehicleOperatingMode = readiness?.VehicleOperatingMode,
            VehicleModel = readiness?.VehicleModel ?? ResolveConfiguredModel(descriptor.DeviceId),
            ControllerVersion = readiness?.ControllerVersion,
            IsFullPreflight = isFullPreflight,
            FullPreflightPassed = fullPreflightPassed,
            BlockingReasons = reasons.ToArray(),
            FullPreflightBlockingReasons = isFullPreflight ? reasons.ToArray() : [],
            ObservedAtUtc = observedAtUtc,
            FullPreflightObservedAtUtc = isFullPreflight
                ? mapEvidence?.ObservedAtUtc ?? readiness?.ObservedAtUtc ?? observedAtUtc
                : null,
            Error = activeTaskReadError
        };
    }

    private string? ResolveConfiguredModel(string deviceId) =>
        profile.Agvs.FirstOrDefault(agv =>
            string.Equals(agv.AgvId, deviceId, StringComparison.OrdinalIgnoreCase))?.Model;

    private static IReadOnlyList<string> GetSnapshotBlockingReasons(
        AgvSnapshotResponse snapshot,
        string? activeTaskReadError)
    {
        var reasons = new List<string>();
        if (!snapshot.Online)
            reasons.Add(PhysicalReadinessReasonCodes.DeviceOffline);
        if (snapshot.CurrentTaskId.HasValue)
            reasons.Add(PhysicalReadinessReasonCodes.ActiveTask);
        if (!string.IsNullOrWhiteSpace(activeTaskReadError))
            reasons.Add("agv_active_task_status_unavailable");
        if (string.Equals(snapshot.ControlOwner, "unknown", StringComparison.OrdinalIgnoreCase))
            reasons.Add("agv_control_owner_unknown");
        if (!string.IsNullOrWhiteSpace(snapshot.ControlOwner) &&
            !string.Equals(snapshot.ControlOwner, "none", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(snapshot.ControlOwner, "adapter", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("agv_control_owned_by_another_client");
        }

        var readiness = snapshot.SafetyReadiness;
        if (readiness is null) return reasons;
        if (readiness.Emergency != false) reasons.Add("emergency_status_not_clear");
        if (readiness.Blocked != false)
            reasons.Add(PhysicalReadinessReasonCodes.TemporaryObstacle);
        if (readiness.ManualBlock == true) reasons.Add("manual_block_active_or_unconfirmed");
        if (readiness.FatalCount > 0 || readiness.ErrorCount > 0)
            reasons.Add("controller_faults_active");
        if (readiness.RelocationStatus != 1) reasons.Add("localization_not_confirmed");
        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string NormalizePreflightReason(string reason) =>
        reason.Trim() switch
        {
            "agv_has_active_task" => PhysicalReadinessReasonCodes.ActiveTask,
            "agv_offline" => PhysicalReadinessReasonCodes.DeviceOffline,
            "blocked_status_not_clear" => PhysicalReadinessReasonCodes.TemporaryObstacle,
            var value => value
        };

    private static bool IsExpectedPolicyBlocker(string reason) =>
        reason is "automatic_dispatch_disabled" or
            "field_navigation_acceptance_disabled" or
            "adapter_does_not_hold_control";

    private static void AddReason(ICollection<string> reasons, string reason)
    {
        if (!reasons.Contains(reason, StringComparer.Ordinal)) reasons.Add(reason);
    }
}

/// <summary>
/// Read-only robot-arm projection.  It uses only IAuboArmReader methods and
/// therefore cannot load, run, stop, write variables, or touch Modbus/DI/DO.
/// </summary>
public sealed class PhysicalAuboReadinessProbe(
    IAuboArmGateway arm,
    TimeProvider timeProvider) : IPhysicalDeviceReadinessProbe
{
    public bool CanProbe(PhysicalDeviceDescriptor device) =>
        string.Equals(
            device.DeviceFamily,
            WorkflowDeviceFamilyIds.RobotArm,
            StringComparison.OrdinalIgnoreCase);

    public async Task<PhysicalDeviceReadinessObservation> ProbeAsync(
        PhysicalDeviceDescriptor device,
        bool fullPreflight,
        CancellationToken cancellationToken)
    {
        var readiness = await arm.GetReadinessAsync(device.DeviceId, cancellationToken);
        var status = readiness.Status;
        var reasons = readiness.BlockingReasons
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!status.Online) AddReason(reasons, PhysicalReadinessReasonCodes.DeviceOffline);
        if (!string.Equals(
                readiness.DeviceId,
                device.DeviceId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                status.DeviceId,
                device.DeviceId,
                StringComparison.OrdinalIgnoreCase))
        {
            AddReason(reasons, "robot_arm_device_identity_mismatch");
        }
        if (status.Mode == AuboArmMode.Unknown)
            AddReason(reasons, "robot_mode_unknown");
        if (status.SafetyMode != AuboArmSafetyMode.Normal)
            AddReason(reasons, "robot_safety_not_normal");
        if (status.OperationalMode is not (AuboArmOperationalMode.Automatic or AuboArmOperationalMode.Disabled))
            AddReason(reasons, "robot_operational_mode_not_allowed");
        if (status.RuntimeState is not (AuboArmRuntimeState.Running or AuboArmRuntimeState.Stopped))
            AddReason(reasons, "robot_runtime_state_not_reconciled");

        var observedAt = readiness.ObservedAtUtc == default
            ? timeProvider.GetUtcNow()
            : readiness.ObservedAtUtc;
        return new PhysicalDeviceReadinessObservation
        {
            DeviceId = device.DeviceId,
            DeviceFamily = device.DeviceFamily,
            ProbeSucceeded = true,
            Online = status.Online,
            RobotMode = status.Mode.ToString(),
            SafetyMode = status.SafetyMode.ToString(),
            RuntimeState = status.RuntimeStatus ?? status.RuntimeState.ToString(),
            OperationalMode = status.OperationalMode.ToString(),
            LoadedProgram = readiness.LoadedProgram,
            IsFullPreflight = true,
            FullPreflightPassed = readiness.Ready && reasons.Count == 0,
            BlockingReasons = reasons.ToArray(),
            FullPreflightBlockingReasons = reasons.ToArray(),
            ObservedAtUtc = observedAt,
            FullPreflightObservedAtUtc = observedAt
        };
    }

    private static void AddReason(ICollection<string> reasons, string reason)
    {
        if (!reasons.Contains(reason, StringComparer.Ordinal)) reasons.Add(reason);
    }
}
