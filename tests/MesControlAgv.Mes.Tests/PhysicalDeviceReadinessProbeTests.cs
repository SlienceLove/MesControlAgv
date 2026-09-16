using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public sealed class PhysicalDeviceReadinessProbeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Active_task_preflight_keeps_its_own_details_or_blocks_without_reusing_another_task(bool taskChanged, bool taskReadFails)
    {
        var initialId = Guid.NewGuid();
        var currentId = taskChanged ? Guid.NewGuid() : initialId;
        var gateway = new RecordingAgvGateway
        {
            CurrentTaskId = initialId, PreflightTaskIdOverride = currentId,
            TaskResponse = new AgvTaskResponse(initialId, "vendor-task", "LM2", "moving", null),
            TaskReadException = taskReadFails ? new TimeoutException("task status unavailable") : null
        };
        var result = await new PhysicalAgvReadinessProbe(gateway, ProfileConfiguration.Default, TimeProvider.System)
            .ProbeAsync(new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true), true, CancellationToken.None);
        Assert.Equal(currentId, result.ActiveTaskId);
        Assert.False(result.FullPreflightPassed);
        Assert.Contains(PhysicalReadinessReasonCodes.ActiveTask, result.BlockingReasons);
        if (taskChanged || taskReadFails)
        {
            Assert.Null(result.ActiveDeviceTaskId);
            Assert.Null(result.ActiveTaskTargetStationId);
        }
        else
        {
            Assert.Equal("vendor-task", result.ActiveDeviceTaskId);
            Assert.Equal("LM2", result.ActiveTaskTargetStationId);
        }
        if (!taskChanged && taskReadFails)
        {
            Assert.Contains("agv_active_task_status_unavailable", result.BlockingReasons);
            Assert.NotNull(result.ActiveTaskError);
        }
        if (taskChanged)
        {
            Assert.Null(result.ActiveTaskError);
            Assert.Null(result.Error);
            Assert.DoesNotContain("agv_active_task_status_unavailable", result.BlockingReasons);
        }
        Assert.Equal(0, gateway.DispatchWrites + gateway.CancelWrites + gateway.CommandWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Agv_probe_does_not_mix_previous_task_details_with_newer_idle_preflight(bool taskReadFails)
    {
        var taskId = Guid.NewGuid();
        var gateway = new RecordingAgvGateway
        {
            CurrentTaskId = taskId,
            ClearTaskDuringPreflight = true,
            TaskResponse = new AgvTaskResponse(taskId, "vendor-task", "LM2", "moving", "old task warning"),
            TaskReadException = taskReadFails ? new TimeoutException("old task read timed out") : null
        };
        var probe = new PhysicalAgvReadinessProbe(gateway, ProfileConfiguration.Default, TimeProvider.System);
        var result = await probe.ProbeAsync(new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true),
            fullPreflight: true, CancellationToken.None);

        Assert.Equal("LM2", result.CurrentStationId);
        Assert.Null(result.ActiveTaskId);
        Assert.Null(result.ActiveDeviceTaskId);
        Assert.Null(result.ActiveTaskTargetStationId);
        Assert.Null(result.ActiveTaskState);
        Assert.Null(result.ActiveTaskError);
        Assert.Null(result.Error);
        Assert.Empty(result.BlockingReasons);
        Assert.True(result.FullPreflightPassed);
        Assert.Equal(0, gateway.DispatchWrites + gateway.CancelWrites + gateway.CommandWrites);
    }

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

    [Theory]
    [InlineData(AuboArmRuntimeState.Stopped)]
    [InlineData(AuboArmRuntimeState.Running)]
    public async Task Aubo_probe_uses_readiness_only_and_never_loads_runs_stops_or_dispatches(AuboArmRuntimeState runtime)
    {
        var gateway = new RecordingAuboGateway { Runtime = runtime };
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
        Assert.Equal(runtime.ToString(), result.RuntimeState);
        Assert.Equal(1, gateway.ReadinessReads);
        Assert.Equal(0, gateway.HandshakeWrites);
        Assert.Equal(0, gateway.LoadWrites);
        Assert.Equal(0, gateway.RunWrites);
        Assert.Equal(0, gateway.StopWrites);
    }

    [Fact]
    public async Task Aubo_normal_running_and_stopped_transitions_preserve_ready_and_authorized_epoch()
    {
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var gateway = new RecordingAuboGateway { Clock = clock };
        var probe = new PhysicalAuboReadinessProbe(gateway, clock);
        var store = new PhysicalReadinessStateStore(clock);
        var descriptor = new PhysicalDeviceDescriptor("ARM-01", WorkflowDeviceFamilyIds.RobotArm, true, ControlEnabled: true);
        store.Configure(true, [descriptor], clock.GetUtcNow());
        store.Apply(descriptor, await probe.ProbeAsync(descriptor, true, CancellationToken.None),
            TimeSpan.FromSeconds(5), true, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(5));
        store.Apply(descriptor, await probe.ProbeAsync(descriptor, true, CancellationToken.None),
            TimeSpan.FromSeconds(5), true, clock.GetUtcNow());
        var initial = store.GetSnapshot();
        var epoch = Assert.Single(initial.Devices).DeviceEpoch;
        Assert.True(store.AcknowledgeAuthorization("ARM-01", epoch, initial.SupervisorInstanceId));

        foreach (var runtime in new[] { AuboArmRuntimeState.Running, AuboArmRuntimeState.Stopped, AuboArmRuntimeState.Running })
        {
            gateway.Runtime = runtime;
            store.Apply(descriptor, await probe.ProbeAsync(descriptor, true, CancellationToken.None),
                TimeSpan.FromSeconds(5), true, clock.GetUtcNow());
            Assert.True(store.IsCurrentAndReady("ARM-01", epoch, initial.SupervisorInstanceId, out var reason), reason);
            var current = Assert.Single(store.GetSnapshot().Devices);
            Assert.Equal(runtime.ToString(), current.RuntimeState);
            Assert.False(current.RequiresReauthorization);
        }
        Assert.Equal(0, gateway.HandshakeWrites + gateway.LoadWrites + gateway.RunWrites + gateway.StopWrites);
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
        public Guid? CurrentTaskId { get; init; }
        public bool ClearTaskDuringPreflight { get; init; }
        public Guid? PreflightTaskIdOverride { get; init; }
        public AgvTaskResponse? TaskResponse { get; init; }
        public Exception? TaskReadException { get; init; }

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
                ClearTaskDuringPreflight ? Snapshot() with { CurrentTaskId = null, CurrentStationId = "LM2" }
                    : Snapshot() with { CurrentTaskId = PreflightTaskIdOverride ?? CurrentTaskId },
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
            TaskReadException is null ? Task.FromResult(TaskResponse) : Task.FromException<AgvTaskResponse?>(TaskReadException);

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
            new(true, ControlOwner, "LM1", CurrentTaskId, "AGV-01", null, _readiness);
    }

    private sealed class RecordingAuboGateway : IAuboArmGateway
    {
        public TimeProvider Clock { get; init; } = TimeProvider.System;
        public AuboArmRuntimeState Runtime { get; set; } = AuboArmRuntimeState.Stopped;
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
                Clock.GetUtcNow()));
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

        private AuboArmStatusResponse Status(string deviceId) => new(
            deviceId,
            "rob1",
            true,
            AuboArmMode.Running,
            8,
            AuboArmSafetyMode.Normal,
            1,
            Runtime,
            6,
            AuboArmOperationalMode.Automatic,
            1,
            Clock.GetUtcNow())
        {
            RuntimeStatus = Runtime.ToString()
        };
    }
}
