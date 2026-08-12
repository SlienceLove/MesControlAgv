using MesControlAgv.Contracts;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Adapter.Services;

/// <summary>
/// Performs only controller reads and reports why a physical navigation command
/// would be blocked. It deliberately never requests control or calls Navigate.
/// </summary>
public sealed class PhysicalAcceptancePreflightService(
    IAgvDeviceClient device,
    ProfileConfiguration profile)
{
    public Task<PhysicalAgvPreflightResponse> GetAsync(CancellationToken cancellationToken) =>
        GetAsync(forFieldNavigationAcceptance: false, requireControlOwnership: true, cancellationToken);

    public Task<PhysicalAgvPreflightResponse> GetForFieldNavigationAcceptanceAsync(CancellationToken cancellationToken) =>
        GetAsync(forFieldNavigationAcceptance: true, requireControlOwnership: true, cancellationToken);

    public Task<PhysicalAgvPreflightResponse> GetBeforeControlForFieldNavigationAcceptanceAsync(
        CancellationToken cancellationToken) =>
        GetAsync(forFieldNavigationAcceptance: true, requireControlOwnership: false, cancellationToken);

    private async Task<PhysicalAgvPreflightResponse> GetAsync(
        bool forFieldNavigationAcceptance,
        bool requireControlOwnership,
        CancellationToken cancellationToken)
    {
        var snapshot = await device.GetSnapshotAsync(cancellationToken);
        var modePolicy = profile.PhysicalAcceptance?.Safety.VehicleOperatingModePolicy;
        if (device is not IPhysicalAgvDeviceClient physicalDevice)
        {
            return new PhysicalAgvPreflightResponse(
                snapshot,
                null,
                false,
                ["physical_preflight_not_supported_by_active_driver"],
                VehicleOperatingModePolicy: modePolicy);
        }

        var readiness = await physicalDevice.GetSafetyReadinessAsync(cancellationToken);
        var mapEvidence = device is IControllerMapEvidenceDeviceClient mapEvidenceDevice
            ? await mapEvidenceDevice.GetControllerMapEvidenceAsync(cancellationToken)
            : null;
        var enrichedSnapshot = snapshot with { SafetyReadiness = readiness };
        var reasons = new List<string>();
        var physical = profile.PhysicalAcceptance;

        if (physical is null)
        {
            reasons.Add("physical_acceptance_profile_not_configured");
        }
        else
        {
            if (requireControlOwnership
                && physical.Safety.RequireControlOwnership
                && !string.Equals(snapshot.ControlOwner, "adapter", StringComparison.Ordinal))
                reasons.Add("adapter_does_not_hold_control");
            if (!string.IsNullOrWhiteSpace(readiness.MapName)
                && !string.Equals(readiness.MapName, physical.MapSnapshot.MapName, StringComparison.Ordinal))
                reasons.Add("controller_map_name_mismatch");
            if (!string.IsNullOrWhiteSpace(readiness.MapMd5)
                && !string.Equals(readiness.MapMd5, physical.MapSnapshot.Md5, StringComparison.OrdinalIgnoreCase))
                reasons.Add("controller_map_md5_mismatch");
            AddMapEvidenceBlockingReasons(physical.MapSnapshot, mapEvidence, reasons);
            if (physical.Safety.RequireNoEmergency && readiness.Emergency != false)
                reasons.Add("emergency_status_not_clear");
            if (physical.Safety.RequireNoBlocked && readiness.Blocked != false)
                reasons.Add("blocked_status_not_clear");
            if (readiness.ManualBlock == true)
                reasons.Add("manual_block_active_or_unconfirmed");
            if (physical.Safety.RequireNoFaults && (readiness.FatalCount > 0 || readiness.ErrorCount > 0))
                reasons.Add("controller_faults_active");
            if (readiness.RelocationStatus != 1)
                reasons.Add("localization_not_confirmed");
            if (readiness.LocalizationConfidence is not { } localizationConfidence
                || !double.IsFinite(localizationConfidence)
                || localizationConfidence < physical.Safety.MinimumLocalizationConfidence)
                reasons.Add("localization_confidence_below_threshold");
            AddVehicleOperatingModeBlockingReasons(profile, physical.Safety, readiness, reasons);
        }

        if (!snapshot.Online) reasons.Add("agv_offline");
        if (snapshot.CurrentTaskId is not null) reasons.Add("agv_has_active_task");
        if (forFieldNavigationAcceptance)
        {
            if (!profile.Features.EnableFieldNavigationAcceptance)
                reasons.Add("field_navigation_acceptance_disabled");
        }
        else if (!profile.Features.EnableAutomaticDispatch)
        {
            reasons.Add("automatic_dispatch_disabled");
        }

        var blockingReasons = reasons.Distinct(StringComparer.Ordinal).ToArray();
        return new PhysicalAgvPreflightResponse(
            enrichedSnapshot,
            readiness,
            blockingReasons.Length == 0,
            blockingReasons,
            mapEvidence,
            modePolicy);
    }

    private static void AddVehicleOperatingModeBlockingReasons(
        ProfileConfiguration profile,
        PhysicalAgvSafetyProfile safety,
        AgvSafetyReadinessResponse readiness,
        ICollection<string> reasons)
    {
        if (string.Equals(readiness.VehicleOperatingMode, "manual", StringComparison.Ordinal))
        {
            reasons.Add("vehicle_automatic_mode_unconfirmed");
            return;
        }

        if (string.Equals(
                safety.VehicleOperatingModePolicy,
                VehicleOperatingModePolicies.VendorFieldRequired,
                StringComparison.Ordinal))
        {
            if (!string.Equals(readiness.VehicleOperatingMode, "automatic", StringComparison.Ordinal))
                reasons.Add("vehicle_automatic_mode_unconfirmed");
            return;
        }

        if (string.Equals(
                safety.VehicleOperatingModePolicy,
                VehicleOperatingModePolicies.NotExposedByApprovedModel,
                StringComparison.Ordinal))
        {
            var expectedModel = profile.Agvs.FirstOrDefault(agv => agv.Enabled)?.Model?.Trim();
            if (string.IsNullOrWhiteSpace(expectedModel)
                || !string.Equals(
                    readiness.VehicleModel?.Trim(),
                    expectedModel,
                    StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("vehicle_model_unconfirmed_for_mode_policy");
            }
            return;
        }

        reasons.Add("vehicle_operating_mode_policy_invalid");
    }

    private static void AddMapEvidenceBlockingReasons(
        ControllerMapSnapshot expected,
        ControllerMapEvidenceResponse? actual,
        ICollection<string> reasons)
    {
        if (actual is null)
        {
            reasons.Add("controller_map_evidence_unavailable");
            return;
        }

        if (!actual.IsControllerAuthoritative)
            reasons.Add("controller_map_evidence_not_authoritative");
        if (string.IsNullOrWhiteSpace(actual.Source))
            reasons.Add("controller_map_evidence_source_missing");
        if (actual.ObservedAtUtc == default || actual.ObservedAtUtc.Offset != TimeSpan.Zero)
            reasons.Add("controller_map_evidence_observation_time_invalid");
        if (!string.Equals(actual.MapName, expected.MapName, StringComparison.Ordinal))
            reasons.Add("controller_map_name_mismatch");
        if (!string.Equals(actual.Version, expected.Version, StringComparison.Ordinal))
            reasons.Add("controller_map_version_mismatch");
        if (!string.Equals(actual.Md5, expected.Md5, StringComparison.OrdinalIgnoreCase))
            reasons.Add("controller_map_md5_mismatch");

        var expectedStations = expected.StationIds.ToHashSet(StringComparer.Ordinal);
        var actualStationValues = actual.StationIds ?? [];
        var actualStations = actualStationValues
            .Where(station => !string.IsNullOrWhiteSpace(station))
            .ToHashSet(StringComparer.Ordinal);
        if (actualStationValues.Count != actualStations.Count || !actualStations.SetEquals(expectedStations))
            reasons.Add("controller_map_station_catalog_mismatch");

        var expectedEdges = expected.DirectedEdges
            .Select(edge => EdgeKey(edge.From, edge.To))
            .ToHashSet(StringComparer.Ordinal);
        var actualEdgeValues = actual.DirectedEdges ?? [];
        var actualEdges = actualEdgeValues
            .Where(edge => !string.IsNullOrWhiteSpace(edge.From) && !string.IsNullOrWhiteSpace(edge.To))
            .Select(edge => EdgeKey(edge.From, edge.To))
            .ToHashSet(StringComparer.Ordinal);
        if (actualEdgeValues.Count != actualEdges.Count || !actualEdges.SetEquals(expectedEdges))
            reasons.Add("controller_map_directed_edges_mismatch");
    }

    private static string EdgeKey(string from, string to) => $"{from}\u001f{to}";
}

public sealed class PhysicalPreflightRejectedException(IReadOnlyList<string> reasons)
    : InvalidOperationException($"Physical navigation preflight failed: {string.Join(", ", reasons)}")
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}
