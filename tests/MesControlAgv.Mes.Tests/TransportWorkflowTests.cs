using System.Net;
using MesControlAgv.Application;
using MesControlAgv.Contracts;
using MesControlAgv.Domain;
using MesControlAgv.Domain.Profiles;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;
using Microsoft.EntityFrameworkCore;

namespace MesControlAgv.Mes.Tests;

public class TransportWorkflowTests
{
    [Fact]
    public async Task Create_keeps_task_pending_without_calling_adapter()
    {
        var adapter = new FakeAdapterClient();
        var service = CreateService(adapter);

        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);

        Assert.Equal("Created", task.Status);
        Assert.Empty(adapter.OperationIds);
        var detail = await service.GetDetailAsync(task.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.DoesNotContain(detail.Events, item => item.EventType == "DispatchRequested");
    }

    [Fact]
    public async Task Custom_profile_route_dispatches_without_the_legacy_fixed_station_restriction()
    {
        var adapter = new FakeAdapterClient();
        var profile = ProfileConfiguration.Default with
        {
            Stations =
            [
                new StationProfile { Code = 4, StationId = "LM4", AgvStationId = "LM4", Name = "LM4", Type = "PhysicalAcceptance" },
                new StationProfile { Code = 5, StationId = "LM5", AgvStationId = "LM5", Name = "LM5", Type = "PhysicalAcceptance" }
            ],
            Map = new MapProfile
            {
                StationIds = ["LM4", "LM5"],
                Edges = [new MapEdgeProfile { From = "LM4", To = "LM5", Cost = 1, Bidirectional = false }]
            }
        };
        var options = new DbContextOptionsBuilder<MesDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var service = new TaskService(
            new TaskRepository(new MesDbContext(options)),
            adapter,
            profile,
            new PathPlanner(AgvMap.FromProfile(profile.Map)));

        var created = await service.CreateAsync(new(4, 5), CancellationToken.None);
        var task = await service.DispatchAsync(created.Id, CancellationToken.None);

        Assert.Equal("MovingToPickup", task.Status);
        Assert.Equal("LM4", adapter.LastTargetStationId);
    }

    [Fact]
    public async Task Pickup_confirmation_dispatches_dropoff()
    {
        var adapter = new FakeAdapterClient();
        var service = CreateService(adapter);

        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);
        await service.RecordArrivalAsync(task.Id, CancellationToken.None);
        var updated = await service.ConfirmPickupAsync(task.Id, "operator", CancellationToken.None);

        Assert.Equal("MovingToDropoff", updated.Status);
        Assert.Equal("ST_PREP_01", adapter.LastTargetStationId);
    }

    [Fact]
    public async Task Dispatch_records_the_mes_pre_dispatch_plan_before_device_progress()
    {
        var adapter = new FakeAdapterClient();
        var service = CreateService(adapter);

        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);
        var detail = await service.GetDetailAsync(task.Id, CancellationToken.None);

        Assert.NotNull(detail);
        var plan = Assert.Single(detail.Events.Where(item => item.EventType == "PathPlanned"));
        Assert.Contains("mes-pre-dispatch", plan.Payload, StringComparison.Ordinal);
        Assert.Contains("SAMPLE_01", plan.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adapter_conflict_during_dropoff_dispatch_marks_task_failed_with_reason()
    {
        var adapter = new FakeAdapterClient();
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);
        await service.RecordArrivalAsync(task.Id, CancellationToken.None);
        adapter.DispatchException = new AdapterHttpException(
            HttpStatusCode.Conflict,
            "No online, idle AGV controlled by adapter is available.");

        var failed = await service.ConfirmPickupAsync(task.Id, "operator", CancellationToken.None);

        Assert.Equal("Failed", failed.Status);
        Assert.NotNull(failed.EndedAt);
        Assert.Equal("没有可用的空闲 AGV。", failed.LastError);
        var detail = await service.GetDetailAsync(task.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Contains(detail.Events, item => item.EventType == "DeviceFailed");

        adapter.DispatchException = null;
        var retried = await service.RetryAsync(task.Id, CancellationToken.None);

        Assert.Equal("MovingToDropoff", retried.Status);
        Assert.Null(retried.EndedAt);
    }

    [Fact]
    public async Task Completed_task_records_ended_at()
    {
        var adapter = new FakeAdapterClient();
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);

        await service.RecordArrivalAsync(task.Id, CancellationToken.None);
        await service.ConfirmPickupAsync(task.Id, "operator", CancellationToken.None);
        await service.RecordArrivalAsync(task.Id, CancellationToken.None);
        var completed = await service.ConfirmDropoffAsync(task.Id, "operator", CancellationToken.None);

        Assert.Equal("Completed", completed.Status);
        Assert.NotNull(completed.EndedAt);
    }

    [Fact]
    public async Task Failed_task_retries_the_same_pickup_operation_id()
    {
        var adapter = new FakeAdapterClient { DispatchState = "failed" };
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);
        adapter.DispatchState = "moving";

        var retried = await service.RetryAsync(task.Id, CancellationToken.None);

        Assert.Equal("MovingToPickup", retried.Status);
        Assert.Equal(adapter.OperationIds[0], adapter.OperationIds[1]);
    }

    [Fact]
    public async Task Retry_on_a_non_failed_task_does_not_change_task_or_events()
    {
        var adapter = new FakeAdapterClient { ThrowTimeout = true };
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);
        var before = await service.GetDetailAsync(task.Id, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidTaskTransitionException>(() => service.RetryAsync(task.Id, CancellationToken.None));

        var after = await service.GetDetailAsync(task.Id, CancellationToken.None);
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before.Task, after.Task);
        Assert.Equal(before.Events.Count, after.Events.Count);
        Assert.Equal(before.Events.Select(item => item.EventType), after.Events.Select(item => item.EventType));
    }

    [Fact]
    public async Task Cancellation_requires_adapter_device_confirmation()
    {
        var adapter = new FakeAdapterClient { CancelState = "moving" };
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(task.Id, "operator", CancellationToken.None));

        var detail = await service.GetDetailAsync(task.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal("MovingToPickup", detail.Task.Status);
        Assert.DoesNotContain(detail.Events, item => item.EventType == "CancelConfirmed");
    }

    [Fact]
    public async Task Unconfirmed_adapter_cancellation_marks_transport_task_unknown()
    {
        var adapter = new FakeAdapterClient
        {
            CancelState = "unknown",
            CancelError = "cancel_not_confirmed_by_1110"
        };
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);

        var unknown = await service.CancelAsync(task.Id, "operator", CancellationToken.None);

        Assert.Equal("Unknown", unknown.Status);
        Assert.Equal("cancel_not_confirmed_by_1110", unknown.LastError);
        var detail = await service.GetDetailAsync(task.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Contains(detail.Events, item => item.EventType == "Timeout");
        Assert.DoesNotContain(detail.Events, item => item.EventType == "CancelConfirmed");
    }

    [Fact]
    public async Task Confirmed_adapter_cancellation_records_cancel_confirmed()
    {
        var adapter = new FakeAdapterClient { CancelState = "cancelled" };
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);

        var cancelled = await service.CancelAsync(task.Id, "operator", CancellationToken.None);

        Assert.Equal("Cancelled", cancelled.Status);
        Assert.NotNull(cancelled.EndedAt);
    }

    [Fact]
    public async Task Confirmed_pause_is_written_back_to_the_transport_task_and_audit_log()
    {
        var service = CreateService(new FakeAdapterClient());
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);
        var operationId = TransportOperationIds.Pickup(task.Id);

        var paused = await service.RecordAgvCommandAsync(
            operationId,
            "pause",
            new AgvTaskResponse(operationId, operationId.ToString("N"), "SAMPLE_01", "paused", null),
            CancellationToken.None);

        Assert.NotNull(paused);
        Assert.Equal("Paused", paused.Status);
        var detail = await service.GetDetailAsync(task.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Contains(detail.Events, item => item.EventType == "PauseRequested");
    }

    [Fact]
    public async Task Confirmed_resume_restores_the_paused_dropoff_leg()
    {
        var service = CreateService(new FakeAdapterClient());
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);
        await service.RecordArrivalAsync(task.Id, CancellationToken.None);
        await service.ConfirmPickupAsync(task.Id, "operator", CancellationToken.None);
        var operationId = TransportOperationIds.Dropoff(task.Id);
        var paused = await service.RecordAgvCommandAsync(
            operationId,
            "pause",
            new AgvTaskResponse(operationId, operationId.ToString("N"), "ST_PREP_01", "paused", null),
            CancellationToken.None);

        var resumed = await service.RecordAgvCommandAsync(
            operationId,
            "resume",
            new AgvTaskResponse(operationId, operationId.ToString("N"), "ST_PREP_01", "moving", null),
            CancellationToken.None);

        Assert.Equal("Paused", paused?.Status);
        Assert.Equal("MovingToDropoff", resumed?.Status);
    }

    [Fact]
    public async Task Unconfirmed_pause_does_not_change_the_transport_task()
    {
        var service = CreateService(new FakeAdapterClient());
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);
        var operationId = TransportOperationIds.Pickup(task.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordAgvCommandAsync(
            operationId,
            "pause",
            new AgvTaskResponse(operationId, operationId.ToString("N"), "SAMPLE_01", "moving", null),
            CancellationToken.None));

        var detail = await service.GetDetailAsync(task.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal("MovingToPickup", detail.Task.Status);
        Assert.DoesNotContain(detail.Events, item => item.EventType == "PauseRequested");
    }

    [Fact]
    public async Task Repeated_confirmed_pause_and_resume_are_idempotent()
    {
        var service = CreateService(new FakeAdapterClient());
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);
        var operationId = TransportOperationIds.Pickup(task.Id);
        var response = new AgvTaskResponse(operationId, operationId.ToString("N"), "SAMPLE_01", "paused", null);

        await service.RecordAgvCommandAsync(operationId, "pause", response, CancellationToken.None);
        var repeatedPause = await service.RecordAgvCommandAsync(operationId, "pause", response, CancellationToken.None);
        var resumeResponse = response with { State = "moving" };
        await service.RecordAgvCommandAsync(operationId, "resume", resumeResponse, CancellationToken.None);
        var repeatedResume = await service.RecordAgvCommandAsync(operationId, "resume", resumeResponse, CancellationToken.None);

        Assert.Equal("Paused", repeatedPause?.Status);
        Assert.Equal("MovingToPickup", repeatedResume?.Status);
    }

    [Fact]
    public async Task Recovery_maps_arrived_pickup_to_operator_confirmation()
    {
        var adapter = new FakeAdapterClient();
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);
        await service.MarkUnknownAsync(task.Id, CancellationToken.None);
        adapter.Reconciled = new(TransportOperationIds.Pickup(task.Id), "device", "SAMPLE_01", "arrived", null);

        var recovered = await service.RecoverAsync(task.Id, CancellationToken.None);

        Assert.Equal("WaitingPickupConfirmation", recovered.Status);
    }

    [Fact]
    public async Task Startup_recovery_retries_adapter_unavailability_and_leaves_task_unknown()
    {
        var adapter = new FakeAdapterClient { ThrowGetHttpRequest = true };
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);

        await service.ReconcileIncompleteAsync(CancellationToken.None);

        var detail = await service.GetDetailAsync(task.Id, CancellationToken.None);
        Assert.Equal(3, adapter.GetTaskCalls);
        Assert.Equal("Unknown", detail!.Task.Status);
        Assert.Contains(detail.Events, item => item.EventType == "Timeout");
        Assert.DoesNotContain(detail.Events, item => item.EventType.StartsWith("Reconciled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Startup_recovery_retries_transport_cancellation_and_keeps_mes_available()
    {
        var adapter = new FakeAdapterClient { ThrowGetTaskCancellation = true };
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);

        await service.ReconcileIncompleteAsync(CancellationToken.None);

        var detail = await service.GetDetailAsync(task.Id, CancellationToken.None);
        Assert.Equal(3, adapter.GetTaskCalls);
        Assert.Equal("Unknown", detail!.Task.Status);
    }

    [Fact]
    public async Task Startup_recovery_does_not_swallow_stopping_cancellation()
    {
        using var stopping = new CancellationTokenSource();
        var adapter = new FakeAdapterClient { CancelOnGet = stopping };
        var service = CreateService(adapter);
        var task = await service.CreateAsync(new(2, 4), CancellationToken.None);
        await service.DispatchAsync(task.Id, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReconcileIncompleteAsync(stopping.Token));
        Assert.NotEqual(Guid.Empty, task.Id);
    }

    private static TaskService CreateService(FakeAdapterClient adapter)
    {
        var options = new DbContextOptionsBuilder<MesDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new TaskService(new TaskRepository(new MesDbContext(options)), adapter);
    }
}

internal sealed class FakeAdapterClient : IAgvGateway
{
    public string DispatchState { get; set; } = "moving";
    public string? LastTargetStationId { get; private set; }
    public List<Guid> OperationIds { get; } = [];
    public AgvTaskResponse? Reconciled { get; set; }
    public string? CancelState { get; set; }
    public string? CancelError { get; set; }
    public bool ThrowTimeout { get; set; }
    public Exception? DispatchException { get; set; }
    public bool ThrowGetHttpRequest { get; set; }
    public bool ThrowGetTaskCancellation { get; set; }
    public int GetTaskCalls { get; private set; }
    public CancellationTokenSource? CancelOnGet { get; set; }

    public Task<AgvTaskResponse> DispatchAsync(Guid operationId, string targetStationId, CancellationToken cancellationToken)
    {
        OperationIds.Add(operationId);
        LastTargetStationId = targetStationId;
        if (DispatchException is not null) return Task.FromException<AgvTaskResponse>(DispatchException);
        var task = new AgvTaskResponse(operationId, operationId.ToString("N"), targetStationId, DispatchState, DispatchState == "failed" ? "failure" : null);
        return ThrowTimeout
            ? Task.FromException<AgvTaskResponse>(new TimeoutException("adapter timeout"))
            : Task.FromResult(task);
    }

    public Task<AgvTaskResponse?> GetTaskAsync(Guid operationId, CancellationToken cancellationToken)
    {
        GetTaskCalls++;
        if (CancelOnGet is not null)
        {
            CancelOnGet.Cancel();
            return Task.FromException<AgvTaskResponse?>(new TaskCanceledException("adapter request cancelled"));
        }
        if (ThrowGetHttpRequest) return Task.FromException<AgvTaskResponse?>(new HttpRequestException("adapter unavailable"));
        if (ThrowGetTaskCancellation) return Task.FromException<AgvTaskResponse?>(new TaskCanceledException("adapter request timed out"));
        return Task.FromResult(Reconciled);
    }

    public Task<AgvTaskResponse?> CancelAsync(Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult<AgvTaskResponse?>(CancelState is null
            ? null
            : new AgvTaskResponse(operationId, operationId.ToString("N"), "SAMPLE_01", CancelState, CancelError));
    public Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new AgvSnapshotResponse(true, "adapter", null, null));

    public Task<AgvTaskResponse?> ExecuteAgvCommandAsync(
        string agvId,
        string command,
        Guid? taskId,
        CancellationToken cancellationToken) =>
        Task.FromResult<AgvTaskResponse?>(null);
}
