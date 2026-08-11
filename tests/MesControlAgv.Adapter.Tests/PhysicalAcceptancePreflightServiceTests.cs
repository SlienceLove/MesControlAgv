using MesControlAgv.Adapter.Services;
using MesControlAgv.Contracts;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Adapter.Tests;

public sealed class PhysicalAcceptancePreflightServiceTests
{
    [Theory]
    [InlineData("control-owner", "adapter_does_not_hold_control")]
    [InlineData("map-name", "controller_map_name_mismatch")]
    [InlineData("map-md5", "controller_map_md5_mismatch")]
    [InlineData("emergency", "emergency_status_not_clear")]
    [InlineData("blocked", "blocked_status_not_clear")]
    [InlineData("manual-block", "manual_block_active_or_unconfirmed")]
    [InlineData("faults", "controller_faults_active")]
    [InlineData("localization", "localization_not_confirmed")]
    [InlineData("confidence", "localization_confidence_below_threshold")]
    [InlineData("automatic-mode", "vehicle_automatic_mode_unconfirmed")]
    [InlineData("offline", "agv_offline")]
    [InlineData("active-task", "agv_has_active_task")]
    [InlineData("automatic-dispatch", "automatic_dispatch_disabled")]
    [InlineData("map-version", "controller_map_version_mismatch")]
    [InlineData("map-stations", "controller_map_station_catalog_mismatch")]
    [InlineData("map-edges", "controller_map_directed_edges_mismatch")]
    [InlineData("map-evidence-unavailable", "controller_map_evidence_unavailable")]
    [InlineData("map-evidence-untrusted", "controller_map_evidence_not_authoritative")]
    public async Task Preflight_fails_closed_for_each_blocking_gate(
        string gate,
        string expectedReason)
    {
        var device = ReadyDevice();
        var profile = CreateProfile(enableAutomaticDispatch: true);

        switch (gate)
        {
            case "control-owner":
                device.Snapshot = device.Snapshot with { ControlOwner = "operator" };
                break;
            case "map-name":
                device.Readiness = device.Readiness with { MapName = "other-map" };
                break;
            case "map-md5":
                device.Readiness = device.Readiness with { MapMd5 = "00000000000000000000000000000000" };
                break;
            case "emergency":
                device.Readiness = device.Readiness with { Emergency = true };
                break;
            case "blocked":
                device.Readiness = device.Readiness with { Blocked = true };
                break;
            case "manual-block":
                device.Readiness = device.Readiness with { ManualBlock = true };
                break;
            case "faults":
                device.Readiness = device.Readiness with { FatalCount = 1 };
                break;
            case "localization":
                device.Readiness = device.Readiness with { RelocationStatus = 0 };
                break;
            case "confidence":
                device.Readiness = device.Readiness with { LocalizationConfidence = 0.94 };
                break;
            case "automatic-mode":
                device.Readiness = device.Readiness with { VehicleOperatingMode = "unknown" };
                break;
            case "offline":
                device.Snapshot = device.Snapshot with { Online = false };
                break;
            case "active-task":
                device.Snapshot = device.Snapshot with { CurrentTaskId = Guid.NewGuid() };
                break;
            case "automatic-dispatch":
                profile = CreateProfile(enableAutomaticDispatch: false);
                break;
            case "map-version":
                device.MapEvidence = device.MapEvidence! with { Version = "other-version" };
                break;
            case "map-stations":
                device.MapEvidence = device.MapEvidence! with { StationIds = ["LM1"] };
                break;
            case "map-edges":
                device.MapEvidence = device.MapEvidence! with
                {
                    DirectedEdges = [new ControllerDirectedEdgeResponse("LM2", "LM1")]
                };
                break;
            case "map-evidence-unavailable":
                device.MapEvidence = null;
                break;
            case "map-evidence-untrusted":
                device.MapEvidence = device.MapEvidence! with { IsControllerAuthoritative = false };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(gate), gate, null);
        }

        var result = await new PhysicalAcceptancePreflightService(device, profile)
            .GetAsync(CancellationToken.None);

        Assert.False(result.DispatchPermitted);
        Assert.Contains(expectedReason, result.BlockingReasons);
    }

    [Fact]
    public async Task Field_navigation_preflight_requires_its_explicit_feature_gate()
    {
        var device = ReadyDevice();
        var profile = CreateProfile(enableAutomaticDispatch: false, enableFieldNavigationAcceptance: false);

        var result = await new PhysicalAcceptancePreflightService(device, profile)
            .GetForFieldNavigationAcceptanceAsync(CancellationToken.None);

        Assert.False(result.DispatchPermitted);
        Assert.Contains("field_navigation_acceptance_disabled", result.BlockingReasons);
    }

    [Fact]
    public async Task Before_control_preflight_ignores_only_the_control_owner_gate()
    {
        var device = ReadyDevice();
        device.Snapshot = device.Snapshot with { ControlOwner = "none" };
        var profile = CreateProfile(enableAutomaticDispatch: false, enableFieldNavigationAcceptance: true);
        var service = new PhysicalAcceptancePreflightService(device, profile);

        var beforeControl = await service
            .GetBeforeControlForFieldNavigationAcceptanceAsync(CancellationToken.None);
        var afterControl = await service
            .GetForFieldNavigationAcceptanceAsync(CancellationToken.None);

        Assert.True(beforeControl.DispatchPermitted);
        Assert.DoesNotContain("adapter_does_not_hold_control", beforeControl.BlockingReasons);
        Assert.False(afterControl.DispatchPermitted);
        Assert.Contains("adapter_does_not_hold_control", afterControl.BlockingReasons);
    }

    [Fact]
    public async Task Unknown_vehicle_automatic_mode_is_never_treated_as_ready()
    {
        var device = ReadyDevice();
        device.Readiness = device.Readiness with
        {
            VehicleOperatingMode = "unknown",
            VehicleOperatingModeSource = null,
            DispatchMode = 0,
            SrcRelease = false
        };

        var result = await new PhysicalAcceptancePreflightService(
                device,
                CreateProfile(enableAutomaticDispatch: true))
            .GetAsync(CancellationToken.None);

        Assert.False(result.DispatchPermitted);
        Assert.Contains("vehicle_automatic_mode_unconfirmed", result.BlockingReasons);
    }

    [Fact]
    public async Task Approved_model_policy_allows_unknown_vehicle_mode_when_model_matches()
    {
        var device = ReadyDevice();
        device.Readiness = device.Readiness with
        {
            VehicleOperatingMode = "unknown",
            VehicleOperatingModeSource = null,
            VehicleModel = "Vendor-AMR"
        };

        var result = await new PhysicalAcceptancePreflightService(
                device,
                CreateProfile(
                    enableAutomaticDispatch: true,
                    requireAutomaticMode: false,
                    modePolicy: VehicleOperatingModePolicies.NotExposedByApprovedModel))
            .GetAsync(CancellationToken.None);

        Assert.True(result.DispatchPermitted);
        Assert.Equal(VehicleOperatingModePolicies.NotExposedByApprovedModel, result.VehicleOperatingModePolicy);
    }

    [Fact]
    public async Task Approved_model_policy_blocks_unknown_vehicle_mode_when_model_differs()
    {
        var device = ReadyDevice();
        device.Readiness = device.Readiness with
        {
            VehicleOperatingMode = "unknown",
            VehicleOperatingModeSource = null,
            VehicleModel = "Other-AMR"
        };

        var result = await new PhysicalAcceptancePreflightService(
                device,
                CreateProfile(
                    enableAutomaticDispatch: true,
                    requireAutomaticMode: false,
                    modePolicy: VehicleOperatingModePolicies.NotExposedByApprovedModel))
            .GetAsync(CancellationToken.None);

        Assert.False(result.DispatchPermitted);
        Assert.Contains("vehicle_model_unconfirmed_for_mode_policy", result.BlockingReasons);
    }

    [Fact]
    public async Task Approved_model_policy_still_blocks_explicit_manual_mode()
    {
        var device = ReadyDevice();
        device.Readiness = device.Readiness with
        {
            VehicleOperatingMode = "manual",
            VehicleOperatingModeSource = "vendor-1101-mode",
            VehicleModel = "Vendor-AMR"
        };

        var result = await new PhysicalAcceptancePreflightService(
                device,
                CreateProfile(
                    enableAutomaticDispatch: true,
                    requireAutomaticMode: false,
                    modePolicy: VehicleOperatingModePolicies.NotExposedByApprovedModel))
            .GetAsync(CancellationToken.None);

        Assert.False(result.DispatchPermitted);
        Assert.Contains("vehicle_automatic_mode_unconfirmed", result.BlockingReasons);
    }

    [Fact]
    public async Task Preflight_only_reads_snapshot_and_readiness_without_control_or_motion_calls()
    {
        var device = ReadyDevice();
        var result = await new PhysicalAcceptancePreflightService(
                device,
                CreateProfile(enableAutomaticDispatch: true))
            .GetAsync(CancellationToken.None);

        Assert.True(result.DispatchPermitted);
        Assert.Equal(1, device.SnapshotCalls);
        Assert.Equal(1, device.ReadinessCalls);
        Assert.Equal(1, device.MapEvidenceCalls);
        Assert.Equal(0, device.EnsureControlCalls);
        Assert.Equal(0, device.NavigateCalls);
        Assert.Equal(0, device.PauseCalls);
        Assert.Equal(0, device.ResumeCalls);
        Assert.Equal(0, device.CancelCalls);
    }

    [Fact]
    public async Task Authoritative_map_evidence_replaces_missing_realtime_map_fields()
    {
        var device = ReadyDevice();
        device.Readiness = device.Readiness with { MapName = null, MapMd5 = null };

        var result = await new PhysicalAcceptancePreflightService(
                device,
                CreateProfile(enableAutomaticDispatch: true))
            .GetAsync(CancellationToken.None);

        Assert.True(result.DispatchPermitted);
        Assert.DoesNotContain("controller_map_name_mismatch", result.BlockingReasons);
        Assert.DoesNotContain("controller_map_md5_mismatch", result.BlockingReasons);
        Assert.Equal(result.BlockingReasons.Count, result.BlockingReasons.Distinct(StringComparer.Ordinal).Count());
    }

    private static PhysicalAcceptancePreflightDevice ReadyDevice() => new()
    {
        Snapshot = new AgvSnapshotResponse(
            Online: true,
            ControlOwner: "adapter",
            CurrentStationId: "LM1",
            CurrentTaskId: null,
            AgvId: "AGV-01"),
        Readiness = new AgvSafetyReadinessResponse(
            VehicleOperatingMode: "automatic",
            VehicleOperatingModeSource: "vendor-confirmed",
            MapName: "acceptance-map",
            MapMd5: "e1b8d6b2b24362c1d44f1884c0abd8fb",
            ForkAutomatic: true,
            DispatchMode: 1,
            ManualBlock: false,
            SrcRelease: true,
            Emergency: false,
            Blocked: false,
            FatalCount: 0,
            ErrorCount: 0,
            RelocationStatus: 1,
            LocalizationConfidence: 0.99,
            ObservedAtUtc: DateTimeOffset.UtcNow,
            VehicleModel: "Vendor-AMR",
            ControllerVersion: "test-controller"),
        MapEvidence = new ControllerMapEvidenceResponse(
            IsControllerAuthoritative: true,
            Source: "vendor-read-only-map-export",
            MapName: "acceptance-map",
            Version: "1.0",
            Md5: "e1b8d6b2b24362c1d44f1884c0abd8fb",
            StationIds: ["LM1", "LM2"],
            DirectedEdges: [new ControllerDirectedEdgeResponse("LM1", "LM2")],
            ObservedAtUtc: DateTimeOffset.UtcNow)
    };

    private static ProfileConfiguration CreateProfile(
        bool enableAutomaticDispatch,
        bool enableFieldNavigationAcceptance = false,
        bool requireAutomaticMode = true,
        string modePolicy = VehicleOperatingModePolicies.VendorFieldRequired) => new()
    {
        Product = new ProductProfile { ProductId = "MES-AGV", DisplayName = "Tests", Version = "1.0" },
        Agvs =
        [
            new AgvProfile
            {
                AgvId = "AGV-01",
                Model = "Vendor-AMR",
                Driver = "vendor-tcp",
                MaxSpeedMetersPerSecond = 0.3,
                HomeStationId = "LM1"
            }
        ],
        Stations =
        [
            new StationProfile { Code = 1, StationId = "LM1", AgvStationId = "LM1", Name = "LM1", Type = "Station" },
            new StationProfile { Code = 2, StationId = "LM2", AgvStationId = "LM2", Name = "LM2", Type = "Station" }
        ],
        Map = new MapProfile
        {
            StationIds = ["LM1", "LM2"],
            Edges = [new MapEdgeProfile { From = "LM1", To = "LM2", Cost = 1, Bidirectional = false }]
        },
        PhysicalAcceptance = new PhysicalAcceptanceProfile
        {
            ExpectedControlOwner = "MesControlAgv.Adapter",
            MapSnapshot = new ControllerMapSnapshot
            {
                MapName = "acceptance-map",
                Version = "1.0",
                Md5 = "e1b8d6b2b24362c1d44f1884c0abd8fb",
                CapturedAtUtc = new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero),
                StationIds = ["LM1", "LM2"],
                DirectedEdges = [new DirectedMapEdgeProfile { From = "LM1", To = "LM2" }]
            },
            Safety = new PhysicalAgvSafetyProfile
            {
                MinimumLocalizationConfidence = 0.98,
                MaximumDispatchSpeedMetersPerSecond = 0.3,
                RequireControlOwnership = true,
                RequireNoEmergency = true,
                RequireNoBlocked = true,
                RequireNoFaults = true,
                RequireAutomaticMode = requireAutomaticMode,
                VehicleOperatingModePolicy = modePolicy
            }
        },
        Features = new FeatureFlags
        {
            UseSimulator = false,
            EnableAutomaticDispatch = enableAutomaticDispatch,
            EnableFieldNavigationAcceptance = enableFieldNavigationAcceptance
        },
        Timeouts = new TimeoutOptions()
    };

    private sealed class PhysicalAcceptancePreflightDevice : IAgvDeviceClient, IPhysicalAgvDeviceClient, IControllerMapEvidenceDeviceClient
    {
        public AgvSnapshotResponse Snapshot { get; set; } = null!;
        public AgvSafetyReadinessResponse Readiness { get; set; } = null!;
        public ControllerMapEvidenceResponse? MapEvidence { get; set; }
        public int SnapshotCalls { get; private set; }
        public int ReadinessCalls { get; private set; }
        public int MapEvidenceCalls { get; private set; }
        public int EnsureControlCalls { get; private set; }
        public int NavigateCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public Task EnsureControlAsync(CancellationToken cancellationToken)
        {
            EnsureControlCalls++;
            return Task.CompletedTask;
        }

        public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            SnapshotCalls++;
            return Task.FromResult(Snapshot);
        }

        public Task<AgvSafetyReadinessResponse> GetSafetyReadinessAsync(CancellationToken cancellationToken)
        {
            ReadinessCalls++;
            return Task.FromResult(Readiness);
        }

        public Task<ControllerMapEvidenceResponse?> GetControllerMapEvidenceAsync(CancellationToken cancellationToken)
        {
            MapEvidenceCalls++;
            return Task.FromResult(MapEvidence);
        }

        public Task<AgvTaskResponse?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
            Task.FromResult<AgvTaskResponse?>(null);

        public Task<AgvTaskResponse> NavigateAsync(
            Guid taskId,
            string? sourceStationId,
            string stationId,
            CancellationToken cancellationToken)
        {
            NavigateCalls++;
            return Task.FromResult(new AgvTaskResponse(taskId, taskId.ToString("N"), stationId, "moving", null));
        }

        public Task<AgvTaskResponse?> PauseAsync(Guid taskId, CancellationToken cancellationToken)
        {
            PauseCalls++;
            return Task.FromResult<AgvTaskResponse?>(null);
        }

        public Task<AgvTaskResponse?> ResumeAsync(Guid taskId, CancellationToken cancellationToken)
        {
            ResumeCalls++;
            return Task.FromResult<AgvTaskResponse?>(null);
        }

        public Task<AgvTaskResponse?> CancelAsync(Guid taskId, CancellationToken cancellationToken)
        {
            CancelCalls++;
            return Task.FromResult<AgvTaskResponse?>(null);
        }
    }
}
