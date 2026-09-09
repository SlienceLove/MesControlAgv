using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Contracts.Workflows;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MesControlAgv.Mes.Tests;

public sealed class PhysicalReadinessSupervisorTests
{
    [Fact]
    public void Store_invalidates_the_epoch_across_offline_and_online_recovery()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "test-supervisor");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);
        store.Configure(true, [descriptor], clock.UtcNow);

        store.Apply(
            descriptor,
            Observation(descriptor, online: false, full: true, blockers: [PhysicalReadinessReasonCodes.DeviceOffline]),
            TimeSpan.FromSeconds(5),
            requireFullPreflight: true,
            clock.UtcNow);
        var offline = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(PhysicalDeviceReadinessState.Offline, offline.State);
        Assert.Equal(1, offline.DeviceEpoch);

        clock.Advance(TimeSpan.FromSeconds(1));
        store.Apply(
            descriptor,
            Observation(descriptor, online: true, full: true),
            TimeSpan.FromSeconds(5),
            requireFullPreflight: true,
            clock.UtcNow);
        var stabilizing = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(PhysicalDeviceReadinessState.Stabilizing, stabilizing.State);
        Assert.Equal(2, stabilizing.DeviceEpoch);
        Assert.True(stabilizing.RequiresReauthorization);

        clock.Advance(TimeSpan.FromSeconds(6));
        store.Apply(
            descriptor,
            Observation(descriptor, online: true, full: true),
            TimeSpan.FromSeconds(5),
            requireFullPreflight: true,
            clock.UtcNow);
        var ready = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(PhysicalDeviceReadinessState.Ready, ready.State);
        Assert.Equal(2, ready.DeviceEpoch);
        Assert.False(store.GetSnapshot().SchedulingPermitted);

        Assert.True(store.AcknowledgeAuthorization("AGV-01", ready.DeviceEpoch, store.GetSnapshot().SupervisorInstanceId));
        Assert.True(store.GetSnapshot().SchedulingPermitted);
        Assert.True(store.IsCurrentAndReady("AGV-01", ready.DeviceEpoch, out var reason));
        Assert.Null(reason);
        Assert.False(store.IsCurrentAndReady(
            "AGV-01",
            ready.DeviceEpoch,
            "previous-supervisor",
            out reason));
        Assert.Equal(PhysicalReadinessReasonCodes.SupervisorInstanceMismatch, reason);

        clock.Advance(TimeSpan.FromSeconds(1));
        store.Apply(
            descriptor,
            Observation(descriptor, online: false, full: true, blockers: [PhysicalReadinessReasonCodes.DeviceOffline]),
            TimeSpan.FromSeconds(5),
            requireFullPreflight: true,
            clock.UtcNow);
        var afterPowerOff = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(PhysicalDeviceReadinessState.Offline, afterPowerOff.State);
        Assert.Equal(3, afterPowerOff.DeviceEpoch);
        Assert.False(store.IsCurrentAndReady("AGV-01", ready.DeviceEpoch, out reason));
        Assert.Equal(PhysicalReadinessReasonCodes.EpochMismatch, reason);
    }

    [Fact]
    public void Reconfiguring_with_the_same_descriptor_and_configuration_does_not_advance_epoch()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "configure-stable");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);

        store.Configure(true, [descriptor], clock.UtcNow, configurationFingerprint: "cfg-a");
        store.Apply(descriptor, Observation(descriptor, online: true, full: true), TimeSpan.Zero, true, clock.UtcNow);
        var firstEpoch = Assert.Single(store.GetSnapshot().Devices).DeviceEpoch;

        store.Configure(true, [descriptor with { DeviceId = " AGV-01 " }], clock.UtcNow, configurationFingerprint: "cfg-a");
        store.Configure(true, [descriptor], clock.UtcNow, configurationFingerprint: "cfg-a");

        Assert.Equal(firstEpoch, Assert.Single(store.GetSnapshot().Devices).DeviceEpoch);
    }

    [Fact]
    public void A_descriptor_change_advances_epoch_once_and_reserves_that_epoch_for_first_observation()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "descriptor-change");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);

        store.Configure(true, [descriptor], clock.UtcNow);
        store.Apply(descriptor, Observation(descriptor, online: true, full: true), TimeSpan.Zero, true, clock.UtcNow);
        var firstEpoch = Assert.Single(store.GetSnapshot().Devices).DeviceEpoch;

        var changedDescriptor = descriptor with { ControlEnabled = true };
        store.Configure(true, [changedDescriptor], clock.UtcNow);
        var changed = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(firstEpoch + 1, changed.DeviceEpoch);
        Assert.Contains(PhysicalReadinessReasonCodes.DescriptorChanged, changed.BlockingReasons);

        store.Configure(true, [changedDescriptor], clock.UtcNow);
        Assert.Equal(changed.DeviceEpoch, Assert.Single(store.GetSnapshot().Devices).DeviceEpoch);
        store.Apply(changedDescriptor, Observation(changedDescriptor, online: true, full: true), TimeSpan.Zero, true, clock.UtcNow);
        Assert.Equal(changed.DeviceEpoch, Assert.Single(store.GetSnapshot().Devices).DeviceEpoch);
    }

    [Fact]
    public void Supervisor_configuration_change_clears_ready_and_authorization()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "configuration-change");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);

        store.Configure(true, [descriptor], clock.UtcNow, configurationFingerprint: "cfg-a");
        store.Apply(descriptor, Observation(descriptor, online: true, full: true), TimeSpan.Zero, true, clock.UtcNow);
        var ready = Assert.Single(store.GetSnapshot().Devices);
        var instanceId = store.GetSnapshot().SupervisorInstanceId;
        Assert.True(store.AcknowledgeAuthorization(descriptor.DeviceId, ready.DeviceEpoch, instanceId));

        store.Configure(true, [descriptor], clock.UtcNow, configurationFingerprint: "cfg-b");
        var reset = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(ready.DeviceEpoch + 1, reset.DeviceEpoch);
        Assert.True(reset.RequiresReauthorization);
        Assert.NotEqual(PhysicalDeviceReadinessState.Ready, reset.State);
        Assert.Contains(PhysicalReadinessReasonCodes.ConfigurationChanged, reset.BlockingReasons);
        Assert.False(store.AcknowledgeAuthorization(descriptor.DeviceId, ready.DeviceEpoch, instanceId));
    }

    [Fact]
    public void Removing_and_readding_a_device_does_not_reuse_its_old_epoch()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "remove-readd");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);

        store.Configure(true, [descriptor], clock.UtcNow);
        store.Apply(descriptor, Observation(descriptor, online: true, full: true), TimeSpan.Zero, true, clock.UtcNow);
        var oldEpoch = Assert.Single(store.GetSnapshot().Devices).DeviceEpoch;
        store.Configure(true, [], clock.UtcNow);
        Assert.Empty(store.GetSnapshot().Devices);

        store.Configure(true, [descriptor], clock.UtcNow);
        store.Apply(descriptor, Observation(descriptor, online: true, full: true), TimeSpan.Zero, true, clock.UtcNow);
        Assert.Equal(oldEpoch + 1, Assert.Single(store.GetSnapshot().Devices).DeviceEpoch);
    }

    [Fact]
    public void Authorization_from_an_old_supervisor_instance_fails()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "current-supervisor");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);
        store.Configure(true, [descriptor], clock.UtcNow);
        store.Apply(descriptor, Observation(descriptor, online: true, full: true), TimeSpan.Zero, true, clock.UtcNow);
        var ready = Assert.Single(store.GetSnapshot().Devices);

        Assert.False(store.AcknowledgeAuthorization(descriptor.DeviceId, ready.DeviceEpoch, "old-supervisor"));
        Assert.True(store.AcknowledgeAuthorization(descriptor.DeviceId, ready.DeviceEpoch, "current-supervisor"));
    }

    [Fact]
    public void Only_the_current_instance_and_epoch_can_confirm_authorization()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "instance-and-epoch");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);
        store.Configure(true, [descriptor], clock.UtcNow);
        store.Apply(descriptor, Observation(descriptor, online: true, full: true), TimeSpan.Zero, true, clock.UtcNow);
        var ready = Assert.Single(store.GetSnapshot().Devices);

        Assert.False(store.AcknowledgeAuthorization(descriptor.DeviceId, ready.DeviceEpoch + 1, "instance-and-epoch"));
        Assert.False(store.AcknowledgeAuthorization(descriptor.DeviceId, ready.DeviceEpoch, "other-instance"));
        Assert.True(store.AcknowledgeAuthorization(descriptor.DeviceId, ready.DeviceEpoch, "instance-and-epoch"));
    }

    [Fact]
    public void Map_or_identity_change_invalidates_a_ready_epoch()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "test-supervisor");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);
        store.Configure(true, [descriptor], clock.UtcNow);

        ApplyHealthy(store, descriptor, clock, mapMd5: "aaa");
        var first = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(PhysicalDeviceReadinessState.Stabilizing, first.State);
        clock.Advance(TimeSpan.FromSeconds(6));
        ApplyHealthy(store, descriptor, clock, mapMd5: "aaa");
        first = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(PhysicalDeviceReadinessState.Ready, first.State);
        Assert.True(store.AcknowledgeAuthorization("AGV-01", first.DeviceEpoch, store.GetSnapshot().SupervisorInstanceId));

        clock.Advance(TimeSpan.FromSeconds(1));
        ApplyHealthy(store, descriptor, clock, mapMd5: "bbb");
        var changed = Assert.Single(store.GetSnapshot().Devices);
        Assert.NotEqual(first.DeviceEpoch, changed.DeviceEpoch);
        Assert.Equal(PhysicalDeviceReadinessState.Blocked, changed.State);
        Assert.Contains(PhysicalReadinessReasonCodes.IdentityChanged, changed.BlockingReasons);
        Assert.False(store.IsCurrentAndReady("AGV-01", first.DeviceEpoch, out var reason));
        Assert.Equal(PhysicalReadinessReasonCodes.EpochMismatch, reason);
    }

    [Fact]
    public void Ordinary_partial_observations_preserve_the_authoritative_identity_and_epoch()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "partial-observation-test");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);
        store.Configure(true, [descriptor], clock.UtcNow);

        store.Apply(
            descriptor,
            Observation(descriptor, online: true, full: true),
            TimeSpan.Zero,
            requireFullPreflight: true,
            clock.UtcNow);
        var authorized = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(PhysicalDeviceReadinessState.Ready, authorized.State);
        Assert.True(store.AcknowledgeAuthorization(descriptor.DeviceId, authorized.DeviceEpoch, store.GetSnapshot().SupervisorInstanceId));

        var partial = Observation(descriptor, online: true, full: false) with
        {
            // This is the shape returned by the ordinary status poll: the
            // model may be present, while controller/map identity fields are
            // deliberately not authoritative.
            ControllerVersion = null,
            MapName = null,
            MapVersion = null,
            MapMd5 = null
        };

        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            store.Apply(
                descriptor,
                partial with { ObservedAtUtc = clock.UtcNow },
                TimeSpan.Zero,
                requireFullPreflight: true,
                clock.UtcNow);
        }

        var current = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(authorized.DeviceEpoch, current.DeviceEpoch);
        Assert.Equal(PhysicalDeviceReadinessState.Ready, current.State);
        Assert.False(current.RequiresReauthorization);
        Assert.Equal("field-map", current.MapName);
        Assert.Equal("1", current.MapVersion);
        Assert.Equal("map-1", current.MapMd5);
        Assert.DoesNotContain(PhysicalReadinessReasonCodes.IdentityChanged, current.BlockingReasons);
        Assert.True(store.IsCurrentAndReady(
            descriptor.DeviceId,
            authorized.DeviceEpoch,
            out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void A_real_full_preflight_identity_change_after_partial_polls_advances_epoch_once()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "partial-then-change-test");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);
        store.Configure(true, [descriptor], clock.UtcNow);

        store.Apply(
            descriptor,
            Observation(descriptor, online: true, full: true, mapMd5: "aaa"),
            TimeSpan.Zero,
            requireFullPreflight: true,
            clock.UtcNow);
        var first = Assert.Single(store.GetSnapshot().Devices);
        Assert.True(store.AcknowledgeAuthorization(descriptor.DeviceId, first.DeviceEpoch, store.GetSnapshot().SupervisorInstanceId));

        clock.Advance(TimeSpan.FromSeconds(1));
        var partial = Observation(descriptor, online: true, full: false) with
        {
            ControllerVersion = null,
            MapName = null,
            MapVersion = null,
            MapMd5 = null,
            ObservedAtUtc = clock.UtcNow
        };
        store.Apply(descriptor, partial, TimeSpan.Zero, requireFullPreflight: true, clock.UtcNow);
        var beforeChange = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(first.DeviceEpoch, beforeChange.DeviceEpoch);

        clock.Advance(TimeSpan.FromSeconds(1));
        store.Apply(
            descriptor,
            Observation(descriptor, online: true, full: true, mapMd5: "bbb") with
            {
                ObservedAtUtc = clock.UtcNow
            },
            TimeSpan.Zero,
            requireFullPreflight: true,
            clock.UtcNow);
        var changed = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(first.DeviceEpoch + 1, changed.DeviceEpoch);
        Assert.Contains(PhysicalReadinessReasonCodes.IdentityChanged, changed.BlockingReasons);
        Assert.True(changed.RequiresReauthorization);

        clock.Advance(TimeSpan.FromSeconds(1));
        store.Apply(
            descriptor,
            partial with { ObservedAtUtc = clock.UtcNow },
            TimeSpan.Zero,
            requireFullPreflight: true,
            clock.UtcNow);
        var afterPartial = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(changed.DeviceEpoch, afterPartial.DeviceEpoch);
    }

    [Fact]
    public void Probe_failure_immediately_invalidates_a_previously_ready_device()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "test-supervisor");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);
        store.Configure(true, [descriptor], clock.UtcNow);
        store.Apply(
            descriptor,
            Observation(descriptor, online: true, full: true),
            TimeSpan.Zero,
            requireFullPreflight: true,
            clock.UtcNow);
        var ready = Assert.Single(store.GetSnapshot().Devices);
        Assert.True(store.AcknowledgeAuthorization("AGV-01", ready.DeviceEpoch, store.GetSnapshot().SupervisorInstanceId));

        clock.Advance(TimeSpan.FromSeconds(1));
        store.Apply(
            descriptor,
            new PhysicalDeviceReadinessObservation
            {
                DeviceId = descriptor.DeviceId,
                DeviceFamily = descriptor.DeviceFamily,
                ProbeSucceeded = false,
                Online = null,
                BlockingReasons = [PhysicalReadinessReasonCodes.ProbeUnavailable],
                ObservedAtUtc = clock.UtcNow,
                Error = "status channel unavailable"
            },
            TimeSpan.Zero,
            requireFullPreflight: true,
            clock.UtcNow);

        var degraded = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(PhysicalDeviceReadinessState.Degraded, degraded.State);
        Assert.NotEqual(ready.DeviceEpoch, degraded.DeviceEpoch);
        Assert.False(store.IsCurrentAndReady("AGV-01", ready.DeviceEpoch, out var reason));
        Assert.Equal(PhysicalReadinessReasonCodes.EpochMismatch, reason);
    }

    [Fact]
    public void Stale_observation_invalidates_ready_even_when_no_probe_exception_was_reported()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "test-supervisor");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);
        store.Configure(
            true,
            [descriptor],
            clock.UtcNow,
            observationStaleAfter: TimeSpan.FromSeconds(10));
        store.Apply(
            descriptor,
            Observation(descriptor, online: true, full: true),
            TimeSpan.Zero,
            requireFullPreflight: true,
            clock.UtcNow);
        var ready = Assert.Single(store.GetSnapshot().Devices);
        Assert.True(store.AcknowledgeAuthorization("AGV-01", ready.DeviceEpoch, store.GetSnapshot().SupervisorInstanceId));

        clock.Advance(TimeSpan.FromSeconds(11));
        var stale = Assert.Single(store.GetSnapshot().Devices);

        Assert.Equal(PhysicalDeviceReadinessState.Degraded, stale.State);
        Assert.NotEqual(ready.DeviceEpoch, stale.DeviceEpoch);
        Assert.Contains(PhysicalReadinessReasonCodes.ObservationStale, stale.BlockingReasons);
        Assert.False(store.IsCurrentAndReady("AGV-01", ready.DeviceEpoch, out var reason));
        Assert.Equal(PhysicalReadinessReasonCodes.EpochMismatch, reason);
    }

    [Fact]
    public void Multiple_devices_are_isolated_and_active_task_blocks_only_its_device()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "test-supervisor");
        var agv = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);
        var arm = new PhysicalDeviceDescriptor("ARM-01", WorkflowDeviceFamilyIds.RobotArm, true, ControlEnabled: true);
        store.Configure(true, [agv, arm], clock.UtcNow);

        ApplyHealthy(store, agv, clock);
        ApplyHealthy(store, arm, clock);
        clock.Advance(TimeSpan.FromSeconds(6));
        ApplyHealthy(store, agv, clock);
        ApplyHealthy(store, arm, clock);
        var ready = store.GetSnapshot().Devices.ToDictionary(item => item.DeviceId);
        Assert.All(ready.Values, item => Assert.Equal(PhysicalDeviceReadinessState.Ready, item.State));
        Assert.True(store.AcknowledgeAuthorization("ARM-01", ready["ARM-01"].DeviceEpoch, store.GetSnapshot().SupervisorInstanceId));

        clock.Advance(TimeSpan.FromSeconds(1));
        store.Apply(
            agv,
            Observation(
                agv,
                online: true,
                full: true,
                blockers: [PhysicalReadinessReasonCodes.ActiveTask],
                activeTaskId: Guid.NewGuid()),
            TimeSpan.FromSeconds(5),
            requireFullPreflight: true,
            clock.UtcNow);

        var current = store.GetSnapshot().Devices.ToDictionary(item => item.DeviceId);
        Assert.Equal(PhysicalDeviceReadinessState.Blocked, current["AGV-01"].State);
        Assert.Equal(PhysicalDeviceReadinessState.Ready, current["ARM-01"].State);
        Assert.True(store.IsCurrentAndReady("ARM-01", current["ARM-01"].DeviceEpoch, out _));
    }

    [Fact]
    public void Active_task_temporarily_blocks_but_does_not_invalidate_the_authorized_session()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "active-task-test");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);
        store.Configure(true, [descriptor], clock.UtcNow);

        store.Apply(
            descriptor,
            Observation(descriptor, online: true, full: true),
            TimeSpan.Zero,
            requireFullPreflight: true,
            clock.UtcNow);
        var authorized = Assert.Single(store.GetSnapshot().Devices);
        Assert.True(store.AcknowledgeAuthorization(descriptor.DeviceId, authorized.DeviceEpoch, store.GetSnapshot().SupervisorInstanceId));

        clock.Advance(TimeSpan.FromSeconds(1));
        store.Apply(
            descriptor,
            Observation(
                descriptor,
                online: true,
                full: true,
                blockers:
                [
                    PhysicalReadinessReasonCodes.ActiveTask,
                    PhysicalReadinessReasonCodes.TemporaryObstacle
                ],
                activeTaskId: Guid.NewGuid()),
            TimeSpan.Zero,
            requireFullPreflight: true,
            clock.UtcNow);

        var busy = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(PhysicalDeviceReadinessState.Blocked, busy.State);
        Assert.Equal(authorized.DeviceEpoch, busy.DeviceEpoch);
        Assert.False(busy.RequiresReauthorization);
        Assert.False(store.IsCurrentAndReady(
            descriptor.DeviceId,
            authorized.DeviceEpoch,
            out var busyReason));
        Assert.Equal(PhysicalReadinessReasonCodes.DeviceNotReady, busyReason);

        clock.Advance(TimeSpan.FromSeconds(1));
        store.Apply(
            descriptor,
            Observation(descriptor, online: true, full: true),
            TimeSpan.Zero,
            requireFullPreflight: true,
            clock.UtcNow);

        var recovered = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(PhysicalDeviceReadinessState.Ready, recovered.State);
        Assert.Equal(authorized.DeviceEpoch, recovered.DeviceEpoch);
        Assert.False(recovered.RequiresReauthorization);
        Assert.True(store.IsCurrentAndReady(
            descriptor.DeviceId,
            authorized.DeviceEpoch,
            out var recoveredReason));
        Assert.Null(recoveredReason);
    }

    [Fact]
    public void Safety_blocker_still_invalidates_the_authorized_epoch()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "safety-blocker-test");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);
        store.Configure(true, [descriptor], clock.UtcNow);
        store.Apply(
            descriptor,
            Observation(descriptor, online: true, full: true),
            TimeSpan.Zero,
            requireFullPreflight: true,
            clock.UtcNow);
        var authorized = Assert.Single(store.GetSnapshot().Devices);
        Assert.True(store.AcknowledgeAuthorization(descriptor.DeviceId, authorized.DeviceEpoch, store.GetSnapshot().SupervisorInstanceId));

        clock.Advance(TimeSpan.FromSeconds(1));
        store.Apply(
            descriptor,
            Observation(
                descriptor,
                online: true,
                full: true,
                blockers: ["emergency_status_not_clear"]),
            TimeSpan.Zero,
            requireFullPreflight: true,
            clock.UtcNow);

        var blocked = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(PhysicalDeviceReadinessState.Blocked, blocked.State);
        Assert.NotEqual(authorized.DeviceEpoch, blocked.DeviceEpoch);
        Assert.True(blocked.RequiresReauthorization);
        Assert.False(store.IsCurrentAndReady(
            descriptor.DeviceId,
            authorized.DeviceEpoch,
            out var reason));
        Assert.Equal(PhysicalReadinessReasonCodes.EpochMismatch, reason);
    }

    [Fact]
    public void Ready_failure_then_remove_and_readd_does_not_reuse_the_failure_epoch()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero));
        var store = new PhysicalReadinessStateStore(clock, "failure-remove-readd");
        var descriptor = new PhysicalDeviceDescriptor("AGV-01", WorkflowDeviceFamilyIds.Agv, true);
        store.Configure(true, [descriptor], clock.UtcNow);

        store.Apply(
            descriptor,
            Observation(descriptor, online: true, full: true),
            TimeSpan.Zero,
            requireFullPreflight: true,
            clock.UtcNow);
        var ready = Assert.Single(store.GetSnapshot().Devices);
        var instanceId = store.GetSnapshot().SupervisorInstanceId;
        Assert.True(store.AcknowledgeAuthorization(descriptor.DeviceId, ready.DeviceEpoch, instanceId));

        clock.Advance(TimeSpan.FromSeconds(1));
        store.Apply(
            descriptor,
            Observation(
                descriptor,
                online: true,
                full: true,
                blockers: ["emergency_status_not_clear"]),
            TimeSpan.Zero,
            requireFullPreflight: true,
            clock.UtcNow);
        var failed = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(PhysicalDeviceReadinessState.Blocked, failed.State);
        Assert.Equal(ready.DeviceEpoch + 1, failed.DeviceEpoch);
        Assert.True(failed.RequiresReauthorization);
        Assert.False(store.IsCurrentAndReady(
            descriptor.DeviceId,
            ready.DeviceEpoch,
            instanceId,
            out var failureReason));
        Assert.Equal(PhysicalReadinessReasonCodes.EpochMismatch, failureReason);

        store.Configure(true, [], clock.UtcNow);
        store.Configure(true, [descriptor], clock.UtcNow);
        store.Apply(
            descriptor,
            Observation(descriptor, online: true, full: true),
            TimeSpan.Zero,
            requireFullPreflight: true,
            clock.UtcNow);

        var readded = Assert.Single(store.GetSnapshot().Devices);
        Assert.Equal(failed.DeviceEpoch + 1, readded.DeviceEpoch);
        Assert.True(readded.RequiresReauthorization);
        Assert.False(store.AcknowledgeAuthorization(
            descriptor.DeviceId,
            failed.DeviceEpoch,
            instanceId));
    }

    [Fact]
    public async Task Simulator_profile_never_invokes_a_physical_probe()
    {
        var profile = CreateProfile(useSimulator: true);
        var probe = new RecordingProbe();
        using var provider = BuildProvider(
            profile,
            new PhysicalReadinessSupervisorOptions { Enabled = true },
            probe);
        var supervisor = provider.GetRequiredService<PhysicalReadinessSupervisor>();

        var result = await supervisor.RefreshAsync(forceFull: true, CancellationToken.None);

        Assert.False(result.Enabled);
        Assert.Empty(probe.Calls);
        Assert.Contains(
            PhysicalReadinessReasonCodes.SimulatorProfile,
            result.BlockingReasons);
    }

    [Fact]
    public async Task Enabled_supervisor_reprobes_after_reconnect_and_exposes_epoch_bound_gate()
    {
        var profile = CreateProfile(useSimulator: false);
        var probe = new RecordingProbe();
        using var provider = BuildProvider(
            profile,
            new PhysicalReadinessSupervisorOptions
            {
                Enabled = true,
                ReadyStabilityWindow = TimeSpan.Zero,
                FullPreflightInterval = TimeSpan.FromHours(1)
            },
            probe);
        var supervisor = provider.GetRequiredService<PhysicalReadinessSupervisor>();

        probe.Online = true;
        var first = await supervisor.RefreshAsync(forceFull: true, CancellationToken.None);
        var firstDevice = Assert.Single(first.Devices);
        Assert.Equal(PhysicalDeviceReadinessState.Ready, firstDevice.State);
        Assert.True(firstDevice.RequiresReauthorization);
        Assert.False(supervisor.IsCurrentAndReady("AGV-01", firstDevice.DeviceEpoch, out var reason));
        Assert.Equal(PhysicalReadinessReasonCodes.ReauthorizationRequired, reason);
        // A caller must explicitly bind the current epoch before it can pass
        // the dispatch gate.
        Assert.True(supervisor.AcknowledgeAuthorization("AGV-01", firstDevice.DeviceEpoch, first.SupervisorInstanceId));
        Assert.True(supervisor.IsCurrentAndReady("AGV-01", firstDevice.DeviceEpoch, out reason));
        Assert.Null(reason);

        probe.Online = false;
        var offline = await supervisor.RefreshAsync(forceFull: false, CancellationToken.None);
        var offlineDevice = Assert.Single(offline.Devices);
        Assert.Equal(PhysicalDeviceReadinessState.Offline, offlineDevice.State);
        Assert.NotEqual(firstDevice.DeviceEpoch, offlineDevice.DeviceEpoch);

        probe.Online = true;
        var recovered = await supervisor.RefreshAsync(forceFull: false, CancellationToken.None);
        var recoveredDevice = Assert.Single(recovered.Devices);
        Assert.Equal(PhysicalDeviceReadinessState.Ready, recoveredDevice.State);
        Assert.NotEqual(firstDevice.DeviceEpoch, recoveredDevice.DeviceEpoch);
        Assert.False(supervisor.IsCurrentAndReady("AGV-01", firstDevice.DeviceEpoch, out reason));
        Assert.Equal(PhysicalReadinessReasonCodes.EpochMismatch, reason);
        Assert.Contains(true, probe.FullPreflightCalls);
    }

    private static void ApplyHealthy(
        PhysicalReadinessStateStore store,
        PhysicalDeviceDescriptor descriptor,
        MutableClock clock,
        string mapMd5 = "map-1")
    {
        store.Apply(
            descriptor,
            Observation(descriptor, online: true, full: true, mapMd5: mapMd5),
            TimeSpan.FromSeconds(5),
            requireFullPreflight: true,
            clock.UtcNow);
    }

    private static PhysicalDeviceReadinessObservation Observation(
        PhysicalDeviceDescriptor descriptor,
        bool online,
        bool full,
        IReadOnlyList<string>? blockers = null,
        Guid? activeTaskId = null,
        string mapMd5 = "map-1") => new()
        {
            DeviceId = descriptor.DeviceId,
            DeviceFamily = descriptor.DeviceFamily,
            ProbeSucceeded = true,
            Online = online,
            ActiveTaskId = activeTaskId,
            MapName = "field-map",
            MapVersion = "1",
            MapMd5 = mapMd5,
            VehicleModel = "test-model",
            IsFullPreflight = full,
            FullPreflightPassed = online && blockers is null,
            BlockingReasons = blockers ?? [],
            FullPreflightBlockingReasons = blockers ?? [],
            ObservedAtUtc = new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero)
        };

    private static ServiceProvider BuildProvider(
        ProfileConfiguration profile,
        PhysicalReadinessSupervisorOptions options,
        RecordingProbe probe)
    {
        var services = new ServiceCollection();
        services.AddSingleton(profile);
        services.AddSingleton(options);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<PhysicalReadinessStateStore>();
        services.AddSingleton<IPhysicalReadinessState>(sp =>
            sp.GetRequiredService<PhysicalReadinessStateStore>());
        services.AddSingleton<IPhysicalDeviceReadinessProbe>(probe);
        services.AddSingleton<PhysicalReadinessSupervisor>();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.None));
        return services.BuildServiceProvider();
    }

    private static ProfileConfiguration CreateProfile(bool useSimulator) => new()
    {
        Product = new ProductProfile { ProductId = "MES-AGV", DisplayName = "test", Version = "1" },
        Agvs =
        [
            new AgvProfile
            {
                AgvId = "AGV-01",
                Model = "test-model",
                Driver = useSimulator ? "simulator" : "vendor-tcp",
                Endpoint = "tcp://controller.invalid:19206",
                Enabled = true,
                HomeStationId = "LM1"
            }
        ],
        WorkflowDevices = [],
        Stations = [],
        Map = new MapProfile(),
        Features = new FeatureFlags { UseSimulator = useSimulator },
        Timeouts = new TimeoutOptions()
    };

    private sealed class RecordingProbe : IPhysicalDeviceReadinessProbe
    {
        public bool Online { get; set; }
        public List<bool> FullPreflightCalls { get; } = [];
        public List<string> Calls { get; } = [];

        public bool CanProbe(PhysicalDeviceDescriptor device) =>
            string.Equals(device.DeviceFamily, WorkflowDeviceFamilyIds.Agv, StringComparison.OrdinalIgnoreCase);

        public Task<PhysicalDeviceReadinessObservation> ProbeAsync(
            PhysicalDeviceDescriptor device,
            bool fullPreflight,
            CancellationToken cancellationToken)
        {
            Calls.Add(device.DeviceId);
            FullPreflightCalls.Add(fullPreflight);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(Observation(device, Online, fullPreflight) with
            {
                ObservedAtUtc = now,
                FullPreflightObservedAtUtc = fullPreflight ? now : null
            });
        }
    }

    private sealed class MutableClock(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; } = initial;

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public void Advance(TimeSpan amount) => UtcNow = UtcNow.Add(amount);
    }
}
