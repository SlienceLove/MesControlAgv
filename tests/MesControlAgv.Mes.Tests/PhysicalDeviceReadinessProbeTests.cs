using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public sealed class PhysicalDeviceReadinessProbeTests
{
    [Fact]
    public async Task Agv_probe_reads_snapshot_task_and_full_preflight_without_any_mutation()
    {
        var gateway = new RecordingAgvGateway();
        var profile = new ProfileConfiguration
        {
            Product = new ProductProfile { ProductId = "test", DisplayName = "test", Version = "1" },
            Agvs =
            [
                new AgvProfile
                {
                    AgvId = "AGV-01",
                    Model = "W500-SZ",
                    Driver = "vendor-tcp",
                    Endpoint = "tcp://controller.invalid:19206",
                    HomeStationId = "LM1"
                }
            ],
            WorkflowDevices = [],
            Stations = [],
            Map = new MapProfile(),
            Features = new FeatureFlags { UseSimulator = false },
            Timeouts = new TimeoutOptions()
        };
        var probe = new PhysicalAgvReadinessProbe(gateway, profile, TimeProvider.System);

        var result = await probe.ProbeAsync(
            new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true),
            fullPreflight: true,
            CancellationToken.None);

        Assert.True(result.ProbeSucceeded);
        Assert.True(result.Online);
        Assert.True(result.IsFullPreflight);
        Assert.True(result.FullPreflightPassed);
        Assert.Equal("LM1", result.CurrentStationId);
        Assert.Equal("field-map", result.MapName);
        Assert.Equal("map-md5", result.MapMd5);
        Assert.Equal(1, gateway.FleetReads);
        Assert.Equal(1, gateway.PreflightReads);
        Assert.Equal(0, gateway.DispatchWrites);
        Assert.Equal(0, gateway.CancelWrites);
        Assert.Equal(0, gateway.CommandWrites);
    }

    [Fact]
    public async Task Aubo_probe_uses_readiness_only_and_never_loads_runs_stops_or_dispatches()
    {
        var gateway = new RecordingAuboGateway();
        var probe = new PhysicalAuboReadinessProbe(gateway, TimeProvider.System);

        var result = await probe.ProbeAsync(
            new PhysicalDeviceDescriptor(
                "ARM-01",
                WorkflowDeviceFamilyIds.RobotArm,
                RequiredForScheduling: true,
                ControlEnabled: true),
            fullPreflight: true,
            CancellationToken.None);

        Assert.True(result.FullPreflightPassed);
        Assert.Equal("Running", result.RobotMode);
        Assert.Equal("Normal", result.SafetyMode);
        Assert.Equal("Automatic", result.OperationalMode);
        Assert.Equal("Stopped", result.RuntimeState);
        Assert.Equal(1, gateway.ReadinessReads);
        Assert.Equal(0, gateway.HandshakeWrites);
        Assert.Equal(0, gateway.LoadWrites);
        Assert.Equal(0, gateway.RunWrites);
        Assert.Equal(0, gateway.StopWrites);
    }

    [Fact]
    public async Task Agv_probe_treats_disabled_dispatch_and_unclaimed_control_as_policy_not_device_failure()
    {
        var gateway = new RecordingAgvGateway
        {
            ControlOwner = "none",
            PreflightDispatchPermitted = false,
            PreflightBlockingReasons =
            [
                "automatic_dispatch_disabled",
                "adapter_does_not_hold_control"
            ]
        };
        var profile = new ProfileConfiguration
        {
            Product = new ProductProfile { ProductId = "test", DisplayName = "test", Version = "1" },
            Agvs =
            [
                new AgvProfile
                {
                    AgvId = "AGV-01",
                    Model = "W500-SZ",
                    Driver = "vendor-tcp",
                    Endpoint = "tcp://controller.invalid:19206",
                    HomeStationId = "LM1"
                }
            ],
            WorkflowDevices = [],
            Stations = [],
            Map = new MapProfile(),
            Features = new FeatureFlags { UseSimulator = false },
            Timeouts = new TimeoutOptions()
        };
        var probe = new PhysicalAgvReadinessProbe(gateway, profile, TimeProvider.System);

        var result = await probe.ProbeAsync(
            new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true),
            fullPreflight: true,
            CancellationToken.None);

        Assert.True(result.FullPreflightPassed);
        Assert.Empty(result.BlockingReasons);
        Assert.Equal("none", result.ControlOwner);
        Assert.Equal(0, gateway.DispatchWrites);
        Assert.Equal(0, gateway.CancelWrites);
        Assert.Equal(0, gateway.CommandWrites);
    }

    private sealed class RecordingAgvGateway :
        IAgvGateway,
        IFleetAwareAgvGateway,
        IPhysicalPreflightAgvGateway
    {
        private readonly AgvSafetyReadinessResponse _readiness = new(
            "automatic",
            "vendor",
            "field-map",
            "map-md5",
            true,
            0,
            false,
            false,
            false,
            false,
            0,
            0,
            1,
            0.99,
            DateTimeOffset.UtcNow,
            "W500-SZ",
            "1.0");

        public int FleetReads { get; private set; }
        public int PreflightReads { get; private set; }
        public int DispatchWrites { get; private set; }
        public int CancelWrites { get; private set; }
        public int CommandWrites { get; private set; }
        public string ControlOwner { get; init; } = "adapter";
        public bool PreflightDispatchPermitted { get; init; } = true;
        public IReadOnlyList<string> PreflightBlockingReasons { get; init; } = [];

        public Task<IReadOnlyList<AgvSnapshotResponse>> GetFleetSnapshotAsync(
            CancellationToken cancellationToken)
        {
            FleetReads++;
            return Task.FromResult<IReadOnlyList<AgvSnapshotResponse>>(
                [Snapshot()]);
        }

        public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot());

        public Task<PhysicalAgvPreflightResponse> GetPhysicalPreflightAsync(
            CancellationToken cancellationToken)
        {
            PreflightReads++;
            return Task.FromResult(new PhysicalAgvPreflightResponse(
                Snapshot(),
                _readiness,
                PreflightDispatchPermitted,
                PreflightBlockingReasons,
                new ControllerMapEvidenceResponse(
                    true,
                    "controller-api",
                    "field-map",
                    "1",
                    "map-md5",
                    ["LM1", "LM2"],
                    [new ControllerDirectedEdgeResponse("LM1", "LM2")],
                    DateTimeOffset.UtcNow)));
        }

        public Task<AgvTaskResponse?> GetTaskAsync(
            Guid operationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<AgvTaskResponse?>(null);

        public Task<AgvTaskResponse> DispatchAsync(
            Guid operationId,
            string targetStationId,
            CancellationToken cancellationToken)
        {
            DispatchWrites++;
            throw new InvalidOperationException("AGV dispatch must not be called by a readiness probe.");
        }

        public Task<AgvTaskResponse?> CancelAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {
            CancelWrites++;
            throw new InvalidOperationException("AGV cancellation must not be called by a readiness probe.");
        }

        public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(
            string agvId,
            string command,
            Guid? taskId,
            CancellationToken cancellationToken)
        {
            CommandWrites++;
            throw new InvalidOperationException("AGV command must not be called by a readiness probe.");
        }

        private AgvSnapshotResponse Snapshot() =>
            new(true, ControlOwner, "LM1", null, "AGV-01", null, _readiness);
    }

    private sealed class RecordingAuboGateway : IAuboArmGateway
    {
        public int ReadinessReads { get; private set; }
        public int HandshakeWrites { get; private set; }
        public int LoadWrites { get; private set; }
        public int RunWrites { get; private set; }
        public int StopWrites { get; private set; }

        public Task<AuboArmStatusResponse> GetStatusAsync(
            string deviceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Status(deviceId));

        public Task<AuboArmReadinessResponse> GetReadinessAsync(
            string deviceId,
            CancellationToken cancellationToken)
        {
            ReadinessReads++;
            return Task.FromResult(new AuboArmReadinessResponse(
                deviceId,
                true,
                [],
                Status(deviceId),
                "material-flow",
                DateTimeOffset.UtcNow));
        }

        public Task<AuboArmVariableResponse> GetVariableAsync(
            string deviceId,
            string key,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmVariableResponse(
                deviceId, key, false, null, null, null, null, null, DateTimeOffset.UtcNow));

        public Task<AuboArmHandshakeSnapshotResponse> GetHandshakeSnapshotAsync(
            string deviceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AuboArmHandshakeSnapshotResponse(
                deviceId,
                AuboArmHandshakeState.Idle,
                null,
                null,
                null,
                null,
                null,
                DateTimeOffset.UtcNow));

        public Task<AuboArmHandshakeResultResponse> DispatchAsync(
            string deviceId,
            Guid operationId,
            int commandCode,
            CancellationToken cancellationToken)
        {
            HandshakeWrites++;
            throw new InvalidOperationException("AUBO handshake dispatch must not be called by a readiness probe.");
        }

        public Task<AuboArmProgramOperationResponse> LoadProgramAsync(
            string deviceId,
            string programName,
            string operatorName,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            LoadWrites++;
            throw new InvalidOperationException("AUBO load must not be called by a readiness probe.");
        }

        public Task<AuboArmProgramOperationResponse> RunProgramAsync(
            string deviceId,
            string? programName,
            string operatorName,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            RunWrites++;
            throw new InvalidOperationException("AUBO run must not be called by a readiness probe.");
        }

        public Task<AuboArmProgramOperationResponse> StopProgramAsync(
            string deviceId,
            string operatorName,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            StopWrites++;
            throw new InvalidOperationException("AUBO stop must not be called by a readiness probe.");
        }

        private static AuboArmStatusResponse Status(string deviceId) => new(
            deviceId,
            "rob1",
            true,
            AuboArmMode.Running,
            8,
            AuboArmSafetyMode.Normal,
            1,
            AuboArmRuntimeState.Stopped,
            6,
            AuboArmOperationalMode.Automatic,
            1,
            DateTimeOffset.UtcNow)
        {
            RuntimeStatus = "Stopped"
        };
    }
}
