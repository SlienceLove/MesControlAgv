using System.Net.Sockets;
using MesControlAgv.Contracts;
using MesControlAgv.Adapter.Data;
using MesControlAgv.Adapter.Entities;
using MesControlAgv.Adapter.Services;
using MesControlAgv.Domain.Profiles;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Adapter.Tests;

public class AdapterServiceTests
{
    [Fact]
    public async Task Duplicate_dispatch_does_not_send_a_second_navigation()
    {
        var simulator = new FakeSimulatorClient();
        var service = CreateService(simulator);
        var taskId = Guid.NewGuid();

        var first = await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);
        var second = await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);

        Assert.Equal(first.DeviceTaskId, second.DeviceTaskId);
        Assert.Equal(1, simulator.NavigateCalls);
        Assert.Equal(1, simulator.EnsureControlCalls);
    }

    [Fact]
    public async Task Route_aware_dispatch_forwards_the_complete_planned_path_start()
    {
        var simulator = new FakeSimulatorClient();
        var service = CreateService(simulator);
        var taskId = Guid.NewGuid();

        await service.DispatchAsync(taskId, "SAMPLE_01", "ST_PREP_01", CancellationToken.None);

        Assert.Equal("CHARGE_01", simulator.SourceStationId);
        Assert.Equal(
            ["CHARGE_01", "PICK_01", "SAMPLE_01", "ST_PREP_01"],
            simulator.NavigatePath);
    }

    [Fact]
    public async Task Persisted_path_is_forwarded_for_dispatch_status_and_cancellation()
    {
        var taskId = Guid.NewGuid();
        string[] path = ["CHARGE_01", "PICK_01", "SAMPLE_01", "ST_PREP_01"];
        var simulator = new FakeSimulatorClient
        {
            ReconciledTask = new(taskId, "device-route", "ST_PREP_01", "moving", null)
        };
        var service = CreateService(simulator);

        var dispatched = await service.DispatchAsync(taskId, "SAMPLE_01", "ST_PREP_01", null, path, CancellationToken.None);
        await service.GetAsync(taskId, CancellationToken.None);
        await service.CancelAsync(taskId, CancellationToken.None);

        Assert.Equal(path, dispatched.Path);
        Assert.Equal(path, simulator.NavigatePath);
        Assert.Equal(path, simulator.StatusPath);
        Assert.Equal(path, simulator.CancelPath);
    }

    [Fact]
    public async Task Duplicate_dispatch_checks_control_owner_before_returning_persisted_operation()
    {
        var simulator = new FakeSimulatorClient();
        var service = CreateService(simulator);
        var taskId = Guid.NewGuid();

        await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);
        simulator.Snapshot = new(false, "roboshop", null, null);

        await Assert.ThrowsAsync<ControlUnavailableException>(() => service.DispatchAsync(
            taskId, "SAMPLE_01", CancellationToken.None));

        Assert.Equal(1, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Failed_persisted_dispatch_retries_navigation_with_same_task_id()
    {
        var taskId = Guid.NewGuid();
        var simulator = new FakeSimulatorClient();
        var (service, database) = CreateServiceWithDatabase(simulator);
        database.Tasks.Add(new AdapterTask
        {
            TaskId = taskId,
            DeviceTaskId = "device-failed",
            TargetStationId = "SAMPLE_01",
            State = "failed",
            LastError = "device unavailable"
        });
        await database.SaveChangesAsync();

        var result = await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);
        var persisted = await database.Tasks.FindAsync([taskId]);

        Assert.Equal("moving", result.State);
        Assert.Equal(1, simulator.NavigateCalls);
        Assert.NotNull(persisted);
        Assert.Equal("moving", persisted.State);
    }

    [Fact]
    public async Task Concurrent_dispatches_do_not_retry_a_failed_in_flight_operation()
    {
        var taskId = Guid.NewGuid();
        var navigationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowNavigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var simulator = new FakeSimulatorClient
        {
            ReturnFailed = true,
            NavigationStarted = navigationStarted,
            AllowNavigation = allowNavigation
        };
        var service = CreateService(simulator);

        var first = service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);
        await navigationStarted.Task;
        var second = service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);
        Assert.False(second.IsCompleted);

        allowNavigation.TrySetResult(true);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal("failed", result.State));
        Assert.Equal(1, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Non_adapter_control_owner_blocks_dispatch()
    {
        var simulator = new FakeSimulatorClient { Snapshot = new(false, "roboshop", null, null) };
        var service = CreateService(simulator);

        await Assert.ThrowsAsync<ControlUnavailableException>(() => service.DispatchAsync(
            Guid.NewGuid(), "SAMPLE_01", CancellationToken.None));
    }

    [Fact]
    public async Task Disabled_automatic_dispatch_does_not_acquire_control_or_navigate()
    {
        var simulator = new FakeSimulatorClient();
        var profile = MesControlAgv.Domain.Profiles.ProfileConfiguration.Default with
        {
            Features = MesControlAgv.Domain.Profiles.ProfileConfiguration.Default.Features with
            {
                EnableAutomaticDispatch = false
            }
        };
        var (service, _) = CreateServiceWithDatabase(simulator, profile);

        await Assert.ThrowsAsync<DispatchDisabledException>(() => service.DispatchAsync(
            Guid.NewGuid(), "SAMPLE_01", CancellationToken.None));

        Assert.Equal(0, simulator.EnsureControlCalls);
        Assert.Equal(0, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Field_navigation_failure_before_control_does_not_acquire_control()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.Readiness = simulator.Readiness with
        {
            VehicleOperatingMode = "unknown",
            VehicleOperatingModeSource = null
        };
        var service = CreatePhysicalAcceptanceService(simulator);
        var command = new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]);

        var exception = await Assert.ThrowsAsync<PhysicalPreflightRejectedException>(() =>
            service.DispatchFieldNavigationAcceptanceAsync(Guid.NewGuid(), command, CancellationToken.None));

        Assert.Contains("vehicle_automatic_mode_unconfirmed", exception.Reasons);
        Assert.Equal(0, simulator.EnsureControlCalls);
        Assert.Equal(0, simulator.ReleaseControlCalls);
        Assert.Equal(0, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Field_navigation_acquires_control_between_two_preflight_assessments()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.AcquireControlOnEnsure = true;
        var service = CreatePhysicalAcceptanceService(simulator);
        var command = new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]);

        var result = await service.DispatchFieldNavigationAcceptanceAsync(
            Guid.NewGuid(),
            command,
            CancellationToken.None);

        Assert.Equal("moving", result.State);
        Assert.Equal(1, simulator.EnsureControlCalls);
        Assert.Equal(2, simulator.ReadinessCalls);
        Assert.Equal(2, simulator.MapEvidenceCalls);
        Assert.Equal(1, simulator.NavigateCalls);
        Assert.Equal(0, simulator.ReleaseControlCalls);
    }

    [Fact]
    public async Task Field_navigation_write_preparation_failure_releases_control_when_no_navigation_was_attempted()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.AcquireControlOnEnsure = true;
        simulator.NavigationException = new InvalidOperationException("final safety gate failed");
        var service = CreatePhysicalAcceptanceService(simulator);
        var command = new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DispatchFieldNavigationAcceptanceAsync(Guid.NewGuid(), command, CancellationToken.None));

        Assert.Equal(1, simulator.NavigateCalls);
        Assert.Equal(1, simulator.ReleaseControlCalls);
    }

    [Fact]
    public async Task Field_navigation_write_unknown_does_not_release_control_after_navigation_was_attempted()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.AcquireControlOnEnsure = true;
        simulator.MarkNavigationAttemptBeforeException = true;
        simulator.NavigationException = new IOException("3066 response transport failed");
        var service = CreatePhysicalAcceptanceService(simulator);
        var acceptanceId = Guid.NewGuid();
        var command = new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]);

        await Assert.ThrowsAsync<IOException>(() =>
            service.DispatchFieldNavigationAcceptanceAsync(acceptanceId, command, CancellationToken.None));

        Assert.True(simulator.MayHaveWrittenNavigation(acceptanceId));
        Assert.Equal(0, simulator.ReleaseControlCalls);
    }

    [Fact]
    public async Task Field_navigation_timeout_before_write_releases_session_control_and_preserves_timeout()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.AcquireControlOnEnsure = true;
        simulator.NavigationException = new TimeoutException("3066 connection timed out before write");
        var service = CreatePhysicalAcceptanceService(simulator);
        var acceptanceId = Guid.NewGuid();
        var command = new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            service.DispatchFieldNavigationAcceptanceAsync(acceptanceId, command, CancellationToken.None));

        Assert.Equal("3066 connection timed out before write", exception.Message);
        Assert.False(simulator.MayHaveWrittenNavigation(acceptanceId));
        Assert.Equal(1, simulator.ReleaseControlCalls);
    }

    [Fact]
    public async Task Field_navigation_failure_does_not_release_control_inherited_by_the_session()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "adapter" };
        simulator.NavigationException = new InvalidOperationException("final safety gate failed");
        var service = CreatePhysicalAcceptanceService(simulator);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DispatchFieldNavigationAcceptanceAsync(
                Guid.NewGuid(),
                new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]),
                CancellationToken.None));

        Assert.Equal(1, simulator.EnsureControlCalls);
        Assert.Equal(0, simulator.ReleaseControlCalls);
    }

    [Fact]
    public async Task Field_navigation_post_control_rejection_does_not_release_inherited_control()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "adapter" };
        simulator.RejectAfterFirstReadiness = true;
        var service = CreatePhysicalAcceptanceService(simulator);

        await Assert.ThrowsAsync<PhysicalPreflightRejectedException>(() =>
            service.DispatchFieldNavigationAcceptanceAsync(
                Guid.NewGuid(),
                new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]),
                CancellationToken.None));

        Assert.Equal(0, simulator.ReleaseControlCalls);
        Assert.Equal(0, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Field_navigation_post_control_rejection_releases_control_without_masking_original_reasons()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.AcquireControlOnEnsure = true;
        simulator.RejectAfterFirstReadiness = true;
        simulator.ReleaseControlException = new InvalidOperationException("release transport failed");
        var service = CreatePhysicalAcceptanceService(simulator);
        var command = new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]);

        var exception = await Assert.ThrowsAsync<PhysicalPreflightRejectedException>(() =>
            service.DispatchFieldNavigationAcceptanceAsync(Guid.NewGuid(), command, CancellationToken.None));

        Assert.Contains("vehicle_automatic_mode_unconfirmed", exception.Reasons);
        Assert.Equal(1, simulator.EnsureControlCalls);
        Assert.Equal(1, simulator.ReleaseControlCalls);
        Assert.Equal(0, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Field_navigation_post_control_station_mismatch_releases_control_and_preserves_exception()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.AcquireControlOnEnsure = true;
        simulator.SnapshotAfterControl = simulator.Snapshot with
        {
            ControlOwner = "adapter",
            CurrentStationId = "LM2"
        };
        var service = CreatePhysicalAcceptanceService(simulator);
        var command = new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]);

        var exception = await Assert.ThrowsAsync<AgvUnavailableException>(() =>
            service.DispatchFieldNavigationAcceptanceAsync(Guid.NewGuid(), command, CancellationToken.None));

        Assert.Contains("not LM1", exception.Message);
        Assert.Equal(1, simulator.ReleaseControlCalls);
        Assert.Equal(0, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Field_navigation_post_control_transport_failure_releases_control_and_preserves_exception()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.AcquireControlOnEnsure = true;
        var transportException = new SocketException((int)SocketError.ConnectionReset);
        simulator.PostControlPreflightException = transportException;
        var service = CreatePhysicalAcceptanceService(simulator);
        var command = new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]);

        var exception = await Assert.ThrowsAsync<SocketException>(() =>
            service.DispatchFieldNavigationAcceptanceAsync(Guid.NewGuid(), command, CancellationToken.None));

        Assert.Same(transportException, exception);
        Assert.Equal(1, simulator.ReleaseControlCalls);
        Assert.Equal(0, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Field_navigation_post_control_cancellation_releases_control_and_preserves_exception()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.AcquireControlOnEnsure = true;
        var cancellationException = new OperationCanceledException("post-control preflight canceled");
        simulator.PostControlPreflightException = cancellationException;
        var service = CreatePhysicalAcceptanceService(simulator);
        var command = new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.DispatchFieldNavigationAcceptanceAsync(Guid.NewGuid(), command, CancellationToken.None));

        Assert.Same(cancellationException, exception);
        Assert.Equal(1, simulator.ReleaseControlCalls);
        Assert.Equal(0, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Concurrent_field_navigation_sessions_do_not_release_control_during_another_sessions_preflight()
    {
        var postControlReadinessStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPostControlReadiness = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.AcquireControlOnEnsure = true;
        simulator.RejectOnReadinessCall = 2;
        simulator.PostControlReadinessStarted = postControlReadinessStarted;
        simulator.AllowPostControlReadiness = allowPostControlReadiness;
        var service = CreatePhysicalAcceptanceService(simulator);
        var command = new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]);

        var first = service.DispatchFieldNavigationAcceptanceAsync(Guid.NewGuid(), command, CancellationToken.None);
        await postControlReadinessStarted.Task;
        var second = service.DispatchFieldNavigationAcceptanceAsync(Guid.NewGuid(), command, CancellationToken.None);

        Assert.False(second.IsCompleted);
        Assert.Equal(1, simulator.EnsureControlCalls);

        allowPostControlReadiness.TrySetResult(true);
        await Assert.ThrowsAsync<PhysicalPreflightRejectedException>(() => first);
        var secondResult = await second;

        Assert.Equal("moving", secondResult.State);
        Assert.Equal(1, simulator.ReleaseControlCalls);
        Assert.Equal(1, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Manual_control_release_waits_for_the_physical_dispatch_session()
    {
        var navigationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowNavigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var simulator = CreateReadyPhysicalSimulator();
        simulator.NavigationStarted = navigationStarted;
        simulator.AllowNavigation = allowNavigation;
        var gate = new PhysicalAgvSessionGate();
        var service = CreatePhysicalAcceptanceService(simulator, gate);

        var dispatch = service.DispatchFieldNavigationAcceptanceAsync(
            Guid.NewGuid(),
            new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]),
            CancellationToken.None);
        await navigationStarted.Task;
        var release = service.ReleaseControlAsync(CancellationToken.None);

        Assert.False(release.IsCompleted);
        Assert.Equal(0, simulator.ReleaseControlCalls);

        allowNavigation.TrySetResult(true);
        await dispatch;
        Assert.True(await release);
        Assert.Equal(1, simulator.ReleaseControlCalls);
    }

    [Fact]
    public async Task Cancelled_manual_release_waiter_never_calls_the_device()
    {
        var navigationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowNavigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var simulator = CreateReadyPhysicalSimulator();
        simulator.NavigationStarted = navigationStarted;
        simulator.AllowNavigation = allowNavigation;
        var service = CreatePhysicalAcceptanceService(simulator, new PhysicalAgvSessionGate());

        var dispatch = service.DispatchFieldNavigationAcceptanceAsync(
            Guid.NewGuid(),
            new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]),
            CancellationToken.None);
        await navigationStarted.Task;
        using var cancellation = new CancellationTokenSource();
        var release = service.ReleaseControlAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => release);
        Assert.Equal(0, simulator.ReleaseControlCalls);

        allowNavigation.TrySetResult(true);
        await dispatch;
    }

    [Fact]
    public async Task Standard_physical_dispatch_waits_for_field_navigation_session()
    {
        var firstNavigationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstNavigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var simulator = CreateReadyPhysicalSimulator();
        simulator.NavigationStarted = firstNavigationStarted;
        simulator.AllowNavigation = allowFirstNavigation;
        var profile = CreatePhysicalAcceptanceProfile() with
        {
            Features = CreatePhysicalAcceptanceProfile().Features with { EnableAutomaticDispatch = true }
        };
        var gate = new PhysicalAgvSessionGate();
        var fieldService = CreatePhysicalAcceptanceService(simulator, gate);
        var standardService = CreatePhysicalAcceptanceService(simulator, gate, profile);

        var field = fieldService.DispatchFieldNavigationAcceptanceAsync(
            Guid.NewGuid(),
            new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]),
            CancellationToken.None);
        await firstNavigationStarted.Task;
        var standard = standardService.DispatchAsync(Guid.NewGuid(), "LM2", CancellationToken.None);

        Assert.False(standard.IsCompleted);
        Assert.Equal(1, simulator.NavigateCalls);

        allowFirstNavigation.TrySetResult(true);
        await field;
        await standard;
        Assert.Equal(2, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Standard_physical_dispatch_failure_before_write_releases_session_control()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.AcquireControlOnEnsure = true;
        simulator.NavigationException = new IOException("3066 connection failed before write");
        var profile = CreatePhysicalAcceptanceProfile() with
        {
            Features = CreatePhysicalAcceptanceProfile().Features with { EnableAutomaticDispatch = true }
        };
        var service = CreatePhysicalAcceptanceService(simulator, new PhysicalAgvSessionGate(), profile);
        var taskId = Guid.NewGuid();

        await Assert.ThrowsAsync<IOException>(() => service.DispatchAsync(
            taskId,
            "LM1",
            "LM2",
            "AGV-01",
            ["LM1", "LM2"],
            CancellationToken.None));

        Assert.False(simulator.MayHaveWrittenNavigation(taskId));
        Assert.Equal(1, simulator.EnsureControlCalls);
        Assert.Equal(1, simulator.ReleaseControlCalls);
    }

    [Fact]
    public async Task Standard_physical_dispatch_timeout_before_write_releases_control_and_preserves_timeout()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.AcquireControlOnEnsure = true;
        simulator.NavigationException = new TimeoutException("3066 connection timed out before write");
        var profile = CreatePhysicalAcceptanceProfile() with
        {
            Features = CreatePhysicalAcceptanceProfile().Features with { EnableAutomaticDispatch = true }
        };
        var service = CreatePhysicalAcceptanceService(simulator, new PhysicalAgvSessionGate(), profile);
        var taskId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => service.DispatchAsync(
            taskId,
            "LM1",
            "LM2",
            "AGV-01",
            ["LM1", "LM2"],
            CancellationToken.None));

        Assert.Equal("3066 connection timed out before write", exception.Message);
        Assert.False(simulator.MayHaveWrittenNavigation(taskId));
        Assert.Equal(1, simulator.ReleaseControlCalls);
    }

    [Fact]
    public async Task Standard_physical_dispatch_unknown_write_does_not_release_session_control()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.AcquireControlOnEnsure = true;
        simulator.MarkNavigationAttemptBeforeException = true;
        simulator.NavigationException = new IOException("3066 response transport failed");
        var profile = CreatePhysicalAcceptanceProfile() with
        {
            Features = CreatePhysicalAcceptanceProfile().Features with { EnableAutomaticDispatch = true }
        };
        var service = CreatePhysicalAcceptanceService(simulator, new PhysicalAgvSessionGate(), profile);
        var taskId = Guid.NewGuid();

        await Assert.ThrowsAsync<IOException>(() => service.DispatchAsync(
            taskId,
            "LM1",
            "LM2",
            "AGV-01",
            ["LM1", "LM2"],
            CancellationToken.None));

        Assert.True(simulator.MayHaveWrittenNavigation(taskId));
        Assert.Equal(1, simulator.EnsureControlCalls);
        Assert.Equal(0, simulator.ReleaseControlCalls);
    }

    [Fact]
    public async Task Physical_dispatch_waits_until_manual_release_transaction_finishes()
    {
        var releaseStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var simulator = CreateReadyPhysicalSimulator();
        simulator.ReleaseStarted = releaseStarted;
        simulator.AllowRelease = allowRelease;
        var service = CreatePhysicalAcceptanceService(simulator, new PhysicalAgvSessionGate());

        var release = service.ReleaseControlAsync(CancellationToken.None);
        await releaseStarted.Task;
        var dispatch = service.DispatchFieldNavigationAcceptanceAsync(
            Guid.NewGuid(),
            new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]),
            CancellationToken.None);

        Assert.False(dispatch.IsCompleted);
        Assert.Equal(0, simulator.ReadinessCalls);
        Assert.Equal(0, simulator.NavigateCalls);

        allowRelease.TrySetResult(true);
        Assert.True(await release);
        await dispatch;
        Assert.Equal(1, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Physical_pause_and_resume_are_rejected_before_control_or_device_writes()
    {
        var simulator = CreateReadyPhysicalSimulator();
        var (service, database) = CreatePhysicalAcceptanceServiceWithDatabase(
            simulator,
            new PhysicalAgvSessionGate());
        var taskId = Guid.NewGuid();
        database.Tasks.Add(new AdapterTask
        {
            TaskId = taskId,
            AgvId = "AGV-01",
            DeviceTaskId = taskId.ToString("N"),
            TargetStationId = "LM2",
            State = "moving"
        });
        await database.SaveChangesAsync();

        await Assert.ThrowsAsync<DispatchDisabledException>(() =>
            service.PauseAsync(taskId, CancellationToken.None));
        await Assert.ThrowsAsync<DispatchDisabledException>(() =>
            service.ResumeAsync(taskId, CancellationToken.None));

        Assert.Equal(0, simulator.EnsureControlCalls);
        Assert.Equal(0, simulator.PauseCalls);
        Assert.Equal(0, simulator.ResumeCalls);
    }

    [Fact]
    public async Task Physical_aggregate_pause_and_resume_are_rejected_before_fleet_lookup()
    {
        var simulator = CreateReadyPhysicalSimulator();
        var service = CreatePhysicalAcceptanceService(simulator, new PhysicalAgvSessionGate());

        await Assert.ThrowsAsync<DispatchDisabledException>(() =>
            service.ExecuteCommandAsync("AGV-01", "pause", null, CancellationToken.None));
        await Assert.ThrowsAsync<DispatchDisabledException>(() =>
            service.ExecuteCommandAsync("AGV-01", "resume", null, CancellationToken.None));

        Assert.Equal(0, simulator.SnapshotCalls);
        Assert.Equal(0, simulator.EnsureControlCalls);
        Assert.Equal(0, simulator.PauseCalls);
        Assert.Equal(0, simulator.ResumeCalls);
    }

    [Fact]
    public async Task Physical_cancellation_waits_for_the_same_session_gate()
    {
        var navigationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowNavigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var simulator = CreateReadyPhysicalSimulator();
        simulator.NavigationStarted = navigationStarted;
        simulator.AllowNavigation = allowNavigation;
        var service = CreatePhysicalAcceptanceService(simulator, new PhysicalAgvSessionGate());

        var dispatch = service.DispatchFieldNavigationAcceptanceAsync(
            Guid.NewGuid(),
            new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]),
            CancellationToken.None);
        await navigationStarted.Task;

        var cancel = service.CancelAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(cancel.IsCompleted);
        Assert.Equal(1, simulator.EnsureControlCalls);
        Assert.Equal(0, simulator.CancelCalls);

        allowNavigation.TrySetResult(true);
        await dispatch;
        Assert.Null(await cancel);
        Assert.Equal(1, simulator.EnsureControlCalls);
        Assert.Equal(0, simulator.CancelCalls);
    }

    [Fact]
    public async Task Physical_aggregate_cancellation_waits_before_fleet_lookup()
    {
        var navigationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowNavigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var simulator = CreateReadyPhysicalSimulator();
        simulator.NavigationStarted = navigationStarted;
        simulator.AllowNavigation = allowNavigation;
        var service = CreatePhysicalAcceptanceService(simulator, new PhysicalAgvSessionGate());

        var dispatch = service.DispatchFieldNavigationAcceptanceAsync(
            Guid.NewGuid(),
            new FieldNavigationDispatchCommand("AGV-01", "LM1", "LM2", ["LM1", "LM2"]),
            CancellationToken.None);
        await navigationStarted.Task;
        var snapshotCallsBeforeCancel = simulator.SnapshotCalls;

        var cancel = service.ExecuteCommandAsync("AGV-01", "cancel", Guid.NewGuid(), CancellationToken.None);

        Assert.False(cancel.IsCompleted);
        Assert.Equal(snapshotCallsBeforeCancel, simulator.SnapshotCalls);
        Assert.Equal(0, simulator.CancelCalls);

        allowNavigation.TrySetResult(true);
        await dispatch;
        Assert.Null(await cancel);
        Assert.Equal(snapshotCallsBeforeCancel, simulator.SnapshotCalls);
        Assert.Equal(0, simulator.CancelCalls);
    }

    [Fact]
    public async Task Read_only_mode_rejects_manual_release_before_calling_the_device()
    {
        var simulator = CreateReadyPhysicalSimulator();
        var options = new DbContextOptionsBuilder<AdapterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var service = new AdapterService(
            new AdapterDbContext(options),
            simulator,
            runMode: AdapterRunMode.ReadOnlyPreflight,
            physicalSessionGate: new PhysicalAgvSessionGate());

        await Assert.ThrowsAsync<ReadOnlyPreflightModeException>(() =>
            service.ReleaseControlAsync(CancellationToken.None));

        Assert.Equal(0, simulator.ReleaseControlCalls);
    }

    [Fact]
    public async Task Disabled_task_cancellation_rejects_before_lookup_or_device_call()
    {
        var simulator = CreateReadyPhysicalSimulator();
        var profile = CreatePhysicalAcceptanceProfile() with
        {
            Features = CreatePhysicalAcceptanceProfile().Features with { EnableTaskCancellation = false }
        };
        var service = CreatePhysicalAcceptanceService(simulator, new PhysicalAgvSessionGate(), profile);

        await Assert.ThrowsAsync<DispatchDisabledException>(() => service.CancelAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(0, simulator.EnsureControlCalls);
        Assert.Equal(0, simulator.CancelCalls);
    }

    [Fact]
    public async Task Disabled_aggregate_cancellation_rejects_before_fleet_lookup_or_device_call()
    {
        var simulator = CreateReadyPhysicalSimulator();
        var profile = CreatePhysicalAcceptanceProfile() with
        {
            Features = CreatePhysicalAcceptanceProfile().Features with { EnableTaskCancellation = false }
        };
        var service = CreatePhysicalAcceptanceService(simulator, new PhysicalAgvSessionGate(), profile);

        await Assert.ThrowsAsync<DispatchDisabledException>(() =>
            service.ExecuteCommandAsync("AGV-01", "cancel", null, CancellationToken.None));

        Assert.Equal(0, simulator.SnapshotCalls);
        Assert.Equal(0, simulator.EnsureControlCalls);
        Assert.Equal(0, simulator.CancelCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("launch")]
    public async Task Invalid_aggregate_command_rejects_before_fleet_lookup_or_device_call(string command)
    {
        var simulator = CreateReadyPhysicalSimulator();
        var service = CreatePhysicalAcceptanceService(simulator, new PhysicalAgvSessionGate());

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            service.ExecuteCommandAsync("AGV-01", command, null, CancellationToken.None));

        Assert.Equal(0, simulator.SnapshotCalls);
        Assert.Equal(0, simulator.EnsureControlCalls);
        Assert.Equal(0, simulator.CancelCalls);
    }

    [Fact]
    public async Task Unknown_task_cancellation_returns_without_acquiring_control()
    {
        var simulator = CreateReadyPhysicalSimulator();
        var service = CreatePhysicalAcceptanceService(simulator, new PhysicalAgvSessionGate());

        var result = await service.CancelAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, simulator.EnsureControlCalls);
        Assert.Equal(0, simulator.CancelCalls);
    }

    [Fact]
    public async Task Physical_cancellation_snapshot_failure_releases_session_control()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.AcquireControlOnEnsure = true;
        var transportException = new IOException("1060 failed after control acquisition");
        simulator.PostControlPreflightException = transportException;
        var (service, database) = CreatePhysicalAcceptanceServiceWithDatabase(
            simulator,
            new PhysicalAgvSessionGate());
        var taskId = Guid.NewGuid();
        database.Tasks.Add(new AdapterTask
        {
            TaskId = taskId,
            AgvId = "AGV-01",
            DeviceTaskId = taskId.ToString("N"),
            TargetStationId = "LM2",
            State = "moving"
        });
        await database.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            service.CancelAsync(taskId, CancellationToken.None));

        Assert.Same(transportException, exception);
        Assert.Equal(1, simulator.EnsureControlCalls);
        Assert.Equal(0, simulator.CancelCalls);
        Assert.Equal(1, simulator.ReleaseControlCalls);
    }

    [Fact]
    public async Task Physical_cancellation_failure_before_write_releases_session_control()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.AcquireControlOnEnsure = true;
        simulator.CancellationException = new IOException("3067 connection failed before write");
        var (service, database) = CreatePhysicalAcceptanceServiceWithDatabase(
            simulator,
            new PhysicalAgvSessionGate());
        var taskId = Guid.NewGuid();
        database.Tasks.Add(new AdapterTask
        {
            TaskId = taskId,
            AgvId = "AGV-01",
            DeviceTaskId = taskId.ToString("N"),
            TargetStationId = "LM2",
            State = "moving"
        });
        await database.SaveChangesAsync();

        await Assert.ThrowsAsync<IOException>(() => service.CancelAsync(taskId, CancellationToken.None));

        Assert.False(simulator.MayHaveWrittenCancellation(taskId));
        Assert.Equal(1, simulator.CancelCalls);
        Assert.Equal(1, simulator.ReleaseControlCalls);
    }

    [Fact]
    public async Task Physical_cancellation_unknown_write_does_not_release_session_control()
    {
        var simulator = CreateReadyPhysicalSimulator();
        simulator.Snapshot = simulator.Snapshot with { ControlOwner = "none" };
        simulator.AcquireControlOnEnsure = true;
        simulator.MarkCancellationWriteBeforeException = true;
        simulator.CancellationException = new IOException("3067 response transport failed");
        var (service, database) = CreatePhysicalAcceptanceServiceWithDatabase(
            simulator,
            new PhysicalAgvSessionGate());
        var taskId = Guid.NewGuid();
        database.Tasks.Add(new AdapterTask
        {
            TaskId = taskId,
            AgvId = "AGV-01",
            DeviceTaskId = taskId.ToString("N"),
            TargetStationId = "LM2",
            State = "moving"
        });
        await database.SaveChangesAsync();

        await Assert.ThrowsAsync<IOException>(() => service.CancelAsync(taskId, CancellationToken.None));

        Assert.True(simulator.MayHaveWrittenCancellation(taskId));
        Assert.Equal(1, simulator.CancelCalls);
        Assert.Equal(0, simulator.ReleaseControlCalls);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("cancel")]
    public async Task Aggregate_command_rejects_a_task_owned_by_another_agv_before_device_calls(string command)
    {
        var simulator = new FakeSimulatorClient
        {
            Snapshot = new AgvSnapshotResponse(true, "adapter", "SAMPLE_01", null, "AGV-01")
        };
        var (service, database) = CreateServiceWithDatabase(simulator);
        var taskId = Guid.NewGuid();
        database.Tasks.Add(new AdapterTask
        {
            TaskId = taskId,
            AgvId = "AGV-02",
            DeviceTaskId = taskId.ToString("N"),
            TargetStationId = "ST_PREP_01",
            State = "moving"
        });
        await database.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AgvUnavailableException>(() =>
            service.ExecuteCommandAsync("AGV-01", command, taskId, CancellationToken.None));

        Assert.Contains("belongs to AGV AGV-02", exception.Message);
        Assert.Equal(0, simulator.SnapshotCalls);
        Assert.Equal(0, simulator.EnsureControlCalls);
        Assert.Equal(0, simulator.PauseCalls);
        Assert.Equal(0, simulator.ResumeCalls);
        Assert.Equal(0, simulator.CancelCalls);
    }

    [Fact]
    public async Task Standard_simulator_dispatch_does_not_release_control()
    {
        var simulator = new FakeSimulatorClient();
        var service = CreateService(simulator);

        await service.DispatchAsync(Guid.NewGuid(), "SAMPLE_01", CancellationToken.None);

        Assert.Equal(0, simulator.ReleaseControlCalls);
    }

    [Fact]
    public async Task Busy_agv_is_rejected_before_a_new_navigation_command()
    {
        var simulator = new FakeSimulatorClient
        {
            Snapshot = new(true, "adapter", "CHARGE_01", Guid.NewGuid())
        };
        var service = CreateService(simulator);

        await Assert.ThrowsAsync<AgvUnavailableException>(() => service.DispatchAsync(
            Guid.NewGuid(), "SAMPLE_01", CancellationToken.None));

        Assert.Equal(0, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Timeout_reconciles_to_actual_device_state_before_unknown()
    {
        var taskId = Guid.NewGuid();
        var simulator = new FakeSimulatorClient
        {
            ThrowTimeout = true,
            ReconciledTask = new(taskId, "device-1", "SAMPLE_01", "moving", null)
        };
        var service = CreateService(simulator);

        var result = await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);

        Assert.Equal("moving", result.State);
        Assert.Equal(1, simulator.StatusCalls);
    }

    [Fact]
    public async Task Get_task_refreshes_persisted_state_from_device()
    {
        var taskId = Guid.NewGuid();
        var simulator = new FakeSimulatorClient
        {
            ReconciledTask = new AgvTaskResponse(taskId, "device-1", "SAMPLE_01", "arrived", null)
        };
        var service = CreateService(simulator);
        await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);

        var task = await service.GetAsync(taskId, CancellationToken.None);

        Assert.NotNull(task);
        Assert.Equal("arrived", task.State);
        Assert.Equal(1, simulator.StatusCalls);
    }

    [Fact]
    public async Task Get_task_marks_active_dispatch_unknown_when_device_status_is_absent()
    {
        var taskId = Guid.NewGuid();
        var simulator = new FakeSimulatorClient();
        var service = CreateService(simulator);
        await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);

        var task = await service.GetAsync(taskId, CancellationToken.None);

        Assert.NotNull(task);
        Assert.Equal("unknown", task.State);
        Assert.Equal("dispatch_not_confirmed_by_1110", task.LastError);
        Assert.Equal(1, simulator.StatusCalls);
        Assert.Equal(1, simulator.NavigateCalls);
    }

    [Fact]
    public async Task Pause_and_resume_persist_confirmed_device_state()
    {
        var simulator = new FakeSimulatorClient();
        var (service, database) = CreateServiceWithDatabase(simulator);
        var taskId = Guid.NewGuid();
        await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);

        var paused = await service.PauseAsync(taskId, CancellationToken.None);
        var persistedPaused = await database.Tasks.FindAsync([taskId]);
        Assert.Equal("paused", paused?.State);
        Assert.Equal("paused", persistedPaused?.State);

        var resumed = await service.ResumeAsync(taskId, CancellationToken.None);
        var persistedResumed = await database.Tasks.FindAsync([taskId]);

        Assert.Equal("moving", resumed?.State);
        Assert.Equal("moving", persistedResumed?.State);
    }

    [Fact]
    public async Task Cancel_persists_only_after_device_confirms_cancellation()
    {
        var taskId = Guid.NewGuid();
        var simulator = new FakeSimulatorClient { CancelState = "moving" };
        var (service, database) = CreateServiceWithDatabase(simulator);
        await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(taskId, CancellationToken.None));

        var persisted = await database.Tasks.FindAsync([taskId]);
        Assert.NotNull(persisted);
        Assert.Equal("moving", persisted.State);
        Assert.Equal(1, simulator.CancelCalls);
    }

    [Fact]
    public async Task Cancel_persists_unknown_when_device_cannot_confirm_cancellation()
    {
        var taskId = Guid.NewGuid();
        var simulator = new FakeSimulatorClient
        {
            CancelState = "unknown",
            CancelError = "cancel_not_confirmed_by_1110"
        };
        var (service, database) = CreateServiceWithDatabase(simulator);
        await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);

        var result = await service.CancelAsync(taskId, CancellationToken.None);

        Assert.Equal("unknown", result!.State);
        Assert.Equal("cancel_not_confirmed_by_1110", result.LastError);
        var persisted = await database.Tasks.FindAsync([taskId]);
        Assert.NotNull(persisted);
        Assert.Equal("unknown", persisted.State);
        Assert.Equal("cancel_not_confirmed_by_1110", persisted.LastError);
    }

    [Fact]
    public async Task Cancel_persists_confirmed_device_cancellation()
    {
        var taskId = Guid.NewGuid();
        var simulator = new FakeSimulatorClient();
        var (service, database) = CreateServiceWithDatabase(simulator);
        await service.DispatchAsync(taskId, "SAMPLE_01", CancellationToken.None);

        var result = await service.CancelAsync(taskId, CancellationToken.None);

        Assert.Equal("cancelled", result!.State);
        Assert.Equal("cancelled", (await database.Tasks.FindAsync([taskId]))!.State);
    }

    [Fact]
    public async Task Fleet_dispatch_assigns_idle_agvs_and_preserves_assignment_on_duplicate()
    {
        var fleet = new FakeFleetClient();
        var device = new FakeSimulatorClient();
        var (service, _) = CreateFleetService(device, fleet);
        var firstTaskId = Guid.NewGuid();
        var secondTaskId = Guid.NewGuid();

        var first = await service.DispatchAsync(firstTaskId, "SAMPLE_01", "ST_PREP_01", CancellationToken.None);
        var second = await service.DispatchAsync(secondTaskId, "SAMPLE_01", "ST_PREP_01", CancellationToken.None);
        var duplicate = await service.DispatchAsync(firstTaskId, "SAMPLE_01", "ST_PREP_01", CancellationToken.None);

        Assert.Equal("AGV-01", first.AgvId);
        Assert.Equal("AGV-02", second.AgvId);
        Assert.Equal(first.AgvId, duplicate.AgvId);
        Assert.NotEmpty(first.Path!);
        Assert.Equal(2, fleet.NavigateCalls);
    }

    private static AdapterService CreateService(FakeSimulatorClient simulator)
    {
        return CreateServiceWithDatabase(simulator).Service;
    }

    private static (AdapterService Service, AdapterDbContext Database) CreateServiceWithDatabase(
        FakeSimulatorClient simulator,
        MesControlAgv.Domain.Profiles.ProfileConfiguration? profile = null)
    {
        var options = new DbContextOptionsBuilder<AdapterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var database = new AdapterDbContext(options);
        return (new AdapterService(database, simulator, profile: profile), database);
    }

    private static AdapterService CreatePhysicalAcceptanceService(
        FakeSimulatorClient simulator,
        PhysicalAgvSessionGate? physicalSessionGate = null,
        ProfileConfiguration? configuredProfile = null)
        => CreatePhysicalAcceptanceServiceWithDatabase(
            simulator,
            physicalSessionGate,
            configuredProfile).Service;

    private static (AdapterService Service, AdapterDbContext Database) CreatePhysicalAcceptanceServiceWithDatabase(
        FakeSimulatorClient simulator,
        PhysicalAgvSessionGate? physicalSessionGate = null,
        ProfileConfiguration? configuredProfile = null)
    {
        var options = new DbContextOptionsBuilder<AdapterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var database = new AdapterDbContext(options);
        var profile = configuredProfile ?? CreatePhysicalAcceptanceProfile();
        var preflight = new PhysicalAcceptancePreflightService(simulator, profile);
        return (new AdapterService(
            database,
            simulator,
            profile: profile,
            physicalPreflight: preflight,
            physicalSessionGate: physicalSessionGate), database);
    }

    private static FakeSimulatorClient CreateReadyPhysicalSimulator() => new()
    {
        Snapshot = new AgvSnapshotResponse(true, "adapter", "LM1", null, "AGV-01"),
        Readiness = new AgvSafetyReadinessResponse(
            VehicleOperatingMode: "automatic",
            VehicleOperatingModeSource: "vendor-1101-mode",
            MapName: null,
            MapMd5: null,
            ForkAutomatic: null,
            DispatchMode: null,
            ManualBlock: false,
            SrcRelease: null,
            Emergency: false,
            Blocked: false,
            FatalCount: 0,
            ErrorCount: 0,
            RelocationStatus: 1,
            LocalizationConfidence: 0.99,
            ObservedAtUtc: DateTimeOffset.UtcNow),
        MapEvidence = new ControllerMapEvidenceResponse(
            IsControllerAuthoritative: true,
            Source: "vendor-tcp:1300,1301,1302,4011",
            MapName: "acceptance-map",
            Version: "1.0",
            Md5: "816e68b9a367d9c8d5eaee9331a7ef58",
            StationIds: ["LM1", "LM2"],
            DirectedEdges: [new ControllerDirectedEdgeResponse("LM1", "LM2")],
            ObservedAtUtc: DateTimeOffset.UtcNow)
    };

    private static ProfileConfiguration CreatePhysicalAcceptanceProfile() => new()
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
                Md5 = "816e68b9a367d9c8d5eaee9331a7ef58",
                CapturedAtUtc = new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero),
                StationIds = ["LM1", "LM2"],
                DirectedEdges = [new DirectedMapEdgeProfile { From = "LM1", To = "LM2" }]
            },
            Safety = new PhysicalAgvSafetyProfile
            {
                MinimumLocalizationConfidence = 0.95,
                MaximumDispatchSpeedMetersPerSecond = 0.3,
                RequireControlOwnership = true,
                RequireNoEmergency = true,
                RequireNoBlocked = true,
                RequireNoFaults = true,
                RequireAutomaticMode = true
            }
        },
        Features = new FeatureFlags
        {
            UseSimulator = false,
            EnableAutomaticDispatch = false,
            EnableFieldNavigationAcceptance = true
        },
        Timeouts = new TimeoutOptions()
    };

    private static (AdapterService Service, AdapterDbContext Database) CreateFleetService(
        FakeSimulatorClient simulator,
        FakeFleetClient fleet)
    {
        var options = new DbContextOptionsBuilder<AdapterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var database = new AdapterDbContext(options);
        var scheduler = new MesControlAgv.Domain.MultiAgvScheduler(
            new MesControlAgv.Domain.PathPlanner(MesControlAgv.Domain.AgvMap.Default));
        return (new AdapterService(database, simulator, fleet, scheduler), database);
    }
}

internal sealed class FakeSimulatorClient : ISimulatorClient, IPhysicalAgvDeviceClient, IControllerMapEvidenceDeviceClient, IControlAcquisitionEvidence, INavigationAttemptState, ICancellationAttemptState
{
    private int _navigateCalls;
    private int _statusCalls;
    private int _cancelCalls;
    private int _pauseCalls;
    private int _resumeCalls;
    private int _readinessCalls;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _navigationAttempts = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _cancellationWrites = new();

    public int NavigateCalls => Volatile.Read(ref _navigateCalls);
    public int StatusCalls => Volatile.Read(ref _statusCalls);
    public int CancelCalls => Volatile.Read(ref _cancelCalls);
    public int PauseCalls => Volatile.Read(ref _pauseCalls);
    public int ResumeCalls => Volatile.Read(ref _resumeCalls);
    public int EnsureControlCalls { get; private set; }
    public int ReleaseControlCalls { get; private set; }
    public int ReadinessCalls => Volatile.Read(ref _readinessCalls);
    public int MapEvidenceCalls { get; private set; }
    public string? SourceStationId { get; private set; }
    public IReadOnlyList<string>? NavigatePath { get; private set; }
    public IReadOnlyList<string>? StatusPath { get; private set; }
    public IReadOnlyList<string>? CancelPath { get; private set; }
    public bool ThrowTimeout { get; init; }
    public bool AcquireControlOnEnsure { get; set; }
    public bool RejectAfterFirstReadiness { get; set; }
    public int? RejectOnReadinessCall { get; set; }
    public Exception? ReleaseControlException { get; set; }
    public Exception? PostControlPreflightException { get; set; }
    public bool ReturnFailed { get; init; }
    public bool MarkNavigationAttemptBeforeException { get; set; }
    public Exception? NavigationException { get; set; }
    public bool MarkCancellationWriteBeforeException { get; set; }
    public Exception? CancellationException { get; set; }
    public string? CancelState { get; init; } = "cancelled";
    public string? CancelError { get; init; }
    public string PauseState { get; init; } = "paused";
    public string ResumeState { get; init; } = "moving";
    public AgvSnapshotResponse Snapshot { get; set; } = new(true, "adapter", "CHARGE_01", null);
    public int SnapshotCalls { get; private set; }
    public AgvSnapshotResponse? SnapshotAfterControl { get; set; }
    public AgvSafetyReadinessResponse Readiness { get; set; } = null!;
    public ControllerMapEvidenceResponse? MapEvidence { get; set; }
    public AgvTaskResponse? ReconciledTask { get; init; }
    public TaskCompletionSource<bool>? NavigationStarted { get; set; }
    public TaskCompletionSource<bool>? AllowNavigation { get; set; }
    public TaskCompletionSource<bool>? PostControlReadinessStarted { get; set; }
    public TaskCompletionSource<bool>? AllowPostControlReadiness { get; set; }
    public TaskCompletionSource<bool>? ReleaseStarted { get; set; }
    public TaskCompletionSource<bool>? AllowRelease { get; set; }

    public Task EnsureControlAsync(CancellationToken cancellationToken)
    {
        EnsureControlCalls++;
        if (AcquireControlOnEnsure) Snapshot = Snapshot with { ControlOwner = "adapter" };
        return Task.CompletedTask;
    }

    public async Task<ControlAcquisitionResult> EnsureControlWithResultAsync(CancellationToken cancellationToken)
    {
        var acquiredByThisCall = AcquireControlOnEnsure && Snapshot.ControlOwner != "adapter";
        await EnsureControlAsync(cancellationToken);
        return new ControlAcquisitionResult(acquiredByThisCall);
    }

    public async Task<bool> ReleaseControlAsync(CancellationToken cancellationToken)
    {
        ReleaseControlCalls++;
        ReleaseStarted?.TrySetResult(true);
        if (AllowRelease is not null) await AllowRelease.Task.WaitAsync(cancellationToken);
        if (ReleaseControlException is not null) throw ReleaseControlException;
        return true;
    }

    public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        SnapshotCalls++;
        if (EnsureControlCalls > 0 && PostControlPreflightException is not null)
            return Task.FromException<AgvSnapshotResponse>(PostControlPreflightException);
        return Task.FromResult(EnsureControlCalls > 0 && SnapshotAfterControl is not null
            ? SnapshotAfterControl
            : Snapshot);
    }

    public async Task<AgvSafetyReadinessResponse> GetSafetyReadinessAsync(CancellationToken cancellationToken)
    {
        var readinessCall = Interlocked.Increment(ref _readinessCalls);
        if (readinessCall > 1 && PostControlReadinessStarted is not null)
        {
            PostControlReadinessStarted.TrySetResult(true);
            if (AllowPostControlReadiness is not null)
                await AllowPostControlReadiness.Task.WaitAsync(cancellationToken);
        }

        return RejectAfterFirstReadiness && readinessCall > 1 || RejectOnReadinessCall == readinessCall
            ? Readiness with { VehicleOperatingMode = "unknown", VehicleOperatingModeSource = null }
            : Readiness;
    }

    public Task<ControllerMapEvidenceResponse?> GetControllerMapEvidenceAsync(CancellationToken cancellationToken)
    {
        MapEvidenceCalls++;
        return Task.FromResult(MapEvidence);
    }

    public Task<AgvTaskResponse?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _statusCalls);
        return Task.FromResult(ReconciledTask);
    }

    public Task<AgvTaskResponse?> GetTaskAsync(Guid taskId, IReadOnlyList<string>? path, CancellationToken cancellationToken)
    {
        StatusPath = path;
        return GetTaskAsync(taskId, cancellationToken);
    }

    public async Task<AgvTaskResponse> NavigateAsync(Guid taskId, string? sourceStationId, string stationId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _navigateCalls);
        SourceStationId = sourceStationId;
        NavigationStarted?.TrySetResult(true);
        if (MarkNavigationAttemptBeforeException) _navigationAttempts.TryAdd(taskId, 0);
        if (NavigationException is not null) throw NavigationException;
        if (ThrowTimeout) throw new TimeoutException();
        if (AllowNavigation is not null) await AllowNavigation.Task.WaitAsync(cancellationToken);
        return new AgvTaskResponse(taskId, $"device-{taskId:N}", stationId, ReturnFailed ? "failed" : "moving", ReturnFailed ? "device unavailable" : null);
    }

    public bool MayHaveWrittenNavigation(Guid taskId) => _navigationAttempts.ContainsKey(taskId);

    public async Task<AgvTaskResponse> NavigateAsync(
        Guid taskId,
        string? sourceStationId,
        string stationId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken)
    {
        NavigatePath = path;
        return (await NavigateAsync(taskId, sourceStationId, stationId, cancellationToken)) with { Path = path };
    }

    public Task<AgvTaskResponse?> PauseAsync(Guid taskId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _pauseCalls);
        return Task.FromResult<AgvTaskResponse?>(
            new AgvTaskResponse(taskId, $"device-{taskId:N}", "SAMPLE_01", PauseState, null));
    }

    public Task<AgvTaskResponse?> ResumeAsync(Guid taskId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _resumeCalls);
        return Task.FromResult<AgvTaskResponse?>(
            new AgvTaskResponse(taskId, $"device-{taskId:N}", "SAMPLE_01", ResumeState, null));
    }

    public Task<AgvTaskResponse?> CancelAsync(Guid taskId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _cancelCalls);
        if (MarkCancellationWriteBeforeException) _cancellationWrites.TryAdd(taskId, 0);
        if (CancellationException is not null) return Task.FromException<AgvTaskResponse?>(CancellationException);
        _cancellationWrites.TryAdd(taskId, 0);
        return Task.FromResult(CancelState is null
            ? null
            : new AgvTaskResponse(taskId, $"device-{taskId:N}", "SAMPLE_01", CancelState, CancelError));
    }

    public Task<AgvTaskResponse?> CancelAsync(Guid taskId, IReadOnlyList<string>? path, CancellationToken cancellationToken)
    {
        CancelPath = path;
        return CancelAsync(taskId, cancellationToken);
    }

    public bool MayHaveWrittenCancellation(Guid taskId) => _cancellationWrites.ContainsKey(taskId);
}

internal sealed class FakeFleetClient : IAgvFleetDeviceClient
{
    private readonly Dictionary<string, AgvSnapshotResponse> _snapshots = new(StringComparer.Ordinal)
    {
        ["AGV-01"] = new(true, "adapter", "CHARGE_01", null, "AGV-01"),
        ["AGV-02"] = new(true, "adapter", "CHARGE_01", null, "AGV-02"),
        ["AGV-03"] = new(true, "adapter", "CHARGE_01", null, "AGV-03")
    };
    private readonly Dictionary<(string AgvId, Guid TaskId), AgvTaskResponse> _tasks = [];

    public int NavigateCalls { get; private set; }

    public Task<IReadOnlyList<AgvSnapshotResponse>> GetFleetSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgvSnapshotResponse>>(_snapshots.Values.ToArray());

    public Task<AgvTaskResponse?> GetTaskAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult(_tasks.GetValueOrDefault((agvId, taskId)));

    public Task<AgvTaskResponse> NavigateAsync(
        string agvId,
        Guid taskId,
        string? sourceStationId,
        string stationId,
        IReadOnlyList<string>? path,
        CancellationToken cancellationToken)
    {
        NavigateCalls++;
        var task = new AgvTaskResponse(taskId, taskId.ToString("N"), stationId, "moving", null, agvId, path);
        _tasks[(agvId, taskId)] = task;
        _snapshots[agvId] = _snapshots[agvId] with { CurrentTaskId = taskId };
        return Task.FromResult(task);
    }

    public Task<AgvTaskResponse?> PauseAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult<AgvTaskResponse?>(_tasks.GetValueOrDefault((agvId, taskId)));

    public Task<AgvTaskResponse?> ResumeAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult<AgvTaskResponse?>(_tasks.GetValueOrDefault((agvId, taskId)));

    public Task<AgvTaskResponse?> CancelAsync(string agvId, Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult<AgvTaskResponse?>(_tasks.GetValueOrDefault((agvId, taskId)) is { } task
            ? task with { State = "cancelled" }
            : null);
}
