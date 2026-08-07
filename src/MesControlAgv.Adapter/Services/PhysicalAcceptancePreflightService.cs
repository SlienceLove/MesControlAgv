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
        GetAsync(forFieldNavigationAcceptance: false, cancellationToken);

    public Task<PhysicalAgvPreflightResponse> GetForFieldNavigationAcceptanceAsync(CancellationToken cancellationToken) =>
        GetAsync(forFieldNavigationAcceptance: true, cancellationToken);

    private async Task<PhysicalAgvPreflightResponse> GetAsync(
        bool forFieldNavigationAcceptance,
        CancellationToken cancellationToken)
    {
        var snapshot = await device.GetSnapshotAsync(cancellationToken);
        if (device is not IPhysicalAgvDeviceClient physicalDevice)
        {
            return new PhysicalAgvPreflightResponse(
                snapshot,
                null,
                false,
                ["physical_preflight_not_supported_by_active_driver"]);
        }

        var readiness = await physicalDevice.GetSafetyReadinessAsync(cancellationToken);
        var enrichedSnapshot = snapshot with { SafetyReadiness = readiness };
        var reasons = new List<string>();
        var physical = profile.PhysicalAcceptance;

        if (physical is null)
        {
            reasons.Add("physical_acceptance_profile_not_configured");
        }
        else
        {
            if (!string.Equals(snapshot.ControlOwner, "adapter", StringComparison.Ordinal))
                reasons.Add("adapter_does_not_hold_control");
            if (!string.Equals(readiness.MapName, physical.MapSnapshot.MapName, StringComparison.Ordinal))
                reasons.Add("controller_map_name_mismatch");
            if (!string.Equals(readiness.MapMd5, physical.MapSnapshot.Md5, StringComparison.OrdinalIgnoreCase))
                reasons.Add("controller_map_md5_mismatch");
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
            if (readiness.LocalizationConfidence is null
                || readiness.LocalizationConfidence < physical.Safety.MinimumLocalizationConfidence)
                reasons.Add("localization_confidence_below_threshold");
            if (physical.Safety.RequireAutomaticMode && readiness.VehicleOperatingMode != "automatic")
                reasons.Add("vehicle_automatic_mode_unconfirmed");
        }

        if (!snapshot.Online) reasons.Add("agv_offline");
        if (forFieldNavigationAcceptance)
        {
            if (!profile.Features.EnableFieldNavigationAcceptance)
                reasons.Add("field_navigation_acceptance_disabled");
        }
        else if (!profile.Features.EnableAutomaticDispatch)
        {
            reasons.Add("automatic_dispatch_disabled");
        }

        return new PhysicalAgvPreflightResponse(enrichedSnapshot, readiness, reasons.Count == 0, reasons);
    }
}

public sealed class PhysicalPreflightRejectedException(IReadOnlyList<string> reasons)
    : InvalidOperationException($"Physical navigation preflight failed: {string.Join(", ", reasons)}")
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}
