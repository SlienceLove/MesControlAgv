using MesControlAgv.Domain;
using MesControlAgv.Mes.Contracts;
using MesControlAgv.Mes.Entities;
using DomainTaskStatus = MesControlAgv.Domain.TaskStatus;

namespace MesControlAgv.Mes.Services;

public sealed class TaskService(TaskRepository repository, IAdapterClient adapter)
{
    public async Task<TaskResponse> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken)
    {
        if (request is not { SourceStationCode: 2, TargetStationCode: 4 }) throw new UnsupportedRouteException();
        var task = await repository.CreateAsync(request.SourceStationCode, request.TargetStationCode, cancellationToken);
        await DispatchLegAsync(task.Id, TaskEvent.PickupMoveStarted, Stations.Get(request.SourceStationCode).AgvStationId, cancellationToken);
        return ToResponse((await repository.GetAsync(task.Id, cancellationToken))!);
    }

    public async Task<TaskResponse> RecordArrivalAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
        var arrival = task.Status == DomainTaskStatus.MovingToPickup ? TaskEvent.PickupArrived : TaskEvent.DropoffArrived;
        return ToResponse(await repository.ApplyEventAsync(taskId, arrival, new { source = "adapter" }, cancellationToken));
    }

    public async Task<TaskResponse> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken)
    {
        await repository.ApplyEventAsync(taskId, TaskEvent.PickupConfirmed, new { operatorName }, cancellationToken);
        var task = await repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
        await DispatchLegAsync(task.Id, TaskEvent.DropoffMoveStarted, Stations.Get(task.TargetStationCode).AgvStationId, cancellationToken);
        return ToResponse((await repository.GetAsync(task.Id, cancellationToken))!);
    }

    public async Task<TaskResponse> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) =>
        ToResponse(await repository.ApplyEventAsync(taskId, TaskEvent.DropoffConfirmed, new { operatorName }, cancellationToken));

    public async Task<TaskResponse> RetryAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await repository.IncrementRetryAsync(taskId, cancellationToken);
        await repository.ApplyEventAsync(taskId, TaskEvent.RetryRequested, new { retry = task.RetryCount }, cancellationToken);
        var target = task.ActiveTargetStationId ?? throw new InvalidOperationException("Task has no target station.");
        var eventType = target == Stations.Get(task.SourceStationCode).AgvStationId ? TaskEvent.PickupMoveStarted : TaskEvent.DropoffMoveStarted;
        await DispatchLegAsync(taskId, eventType, target, cancellationToken);
        return ToResponse((await repository.GetAsync(taskId, cancellationToken))!);
    }

    public async Task<TaskResponse> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken)
    {
        var task = await repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
        var operationId = task.ActiveTargetStationId == Stations.Get(task.TargetStationCode).AgvStationId
            ? TransportOperationIds.Dropoff(task.Id)
            : TransportOperationIds.Pickup(task.Id);
        var cancellation = await adapter.CancelAsync(operationId, cancellationToken);
        if (cancellation?.State != "cancelled") throw new InvalidOperationException("Adapter did not confirm cancellation.");
        return ToResponse(await repository.ApplyEventAsync(taskId, TaskEvent.CancelConfirmed, new { operatorName }, cancellationToken));
    }

    public async Task<TaskResponse> MarkUnknownAsync(Guid taskId, CancellationToken cancellationToken) =>
        ToResponse(await repository.ApplyEventAsync(taskId, TaskEvent.Timeout, new { source = "recovery" }, cancellationToken));

    public async Task<TaskResponse> RecoverAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
        if (task.Status != DomainTaskStatus.Unknown) return ToResponse(task);
        var pickup = task.ActiveTargetStationId == Stations.Get(task.SourceStationCode).AgvStationId;
        var operationId = pickup ? TransportOperationIds.Pickup(task.Id) : TransportOperationIds.Dropoff(task.Id);
        var device = await adapter.GetTaskAsync(operationId, cancellationToken);
        var reconciliation = device?.State switch
        {
            "moving" => pickup ? TaskEvent.ReconciledMoving : TaskEvent.ReconciledMovingToDropoff,
            "arrived" => pickup ? TaskEvent.ReconciledPickupArrived : TaskEvent.ReconciledDropoffArrived,
            "completed" => TaskEvent.ReconciledCompleted,
            "failed" => TaskEvent.ReconciledFailed,
            _ => throw new InvalidOperationException("Device task cannot be reconciled.")
        };
        return ToResponse(await repository.ApplyEventAsync(taskId, reconciliation, new { deviceState = device?.State }, cancellationToken));
    }

    public async Task ReconcileIncompleteAsync(CancellationToken cancellationToken)
    {
        foreach (var status in new[] { DomainTaskStatus.Dispatching, DomainTaskStatus.MovingToPickup, DomainTaskStatus.MovingToDropoff })
        {
            var tasks = await repository.ListByStatusAsync(status, cancellationToken);
            foreach (var task in tasks)
            {
                await repository.ApplyEventAsync(task.Id, TaskEvent.Timeout, new { source = "startup-recovery" }, cancellationToken);
                try
                {
                    await RecoverAsync(task.Id, cancellationToken);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        var unknownTasks = await repository.ListByStatusAsync(DomainTaskStatus.Unknown, cancellationToken);
        foreach (var task in unknownTasks)
        {
            try
            {
                await RecoverAsync(task.Id, cancellationToken);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    public async Task<TaskDetailResponse?> GetDetailAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await repository.GetAsync(taskId, cancellationToken);
        if (task is null) return null;
        var events = await repository.GetEventsAsync(taskId, cancellationToken);
        return new TaskDetailResponse(ToResponse(task), events.Select(ToResponse).ToList());
    }

    public async Task<IReadOnlyList<TaskResponse>> ListAsync(CancellationToken cancellationToken) =>
        (await repository.ListAsync(cancellationToken)).Select(ToResponse).ToList();

    private async Task DispatchLegAsync(Guid taskId, TaskEvent started, string targetStationId, CancellationToken cancellationToken)
    {
        var task = await repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
        if (task.Status == DomainTaskStatus.Created)
        {
            await repository.ApplyEventAsync(taskId, TaskEvent.DispatchRequested, new { targetStationId }, cancellationToken);
        }
        await repository.SetActiveTargetAsync(taskId, targetStationId, cancellationToken);
        var operationId = started == TaskEvent.PickupMoveStarted ? TransportOperationIds.Pickup(taskId) : TransportOperationIds.Dropoff(taskId);
        try
        {
            var response = await adapter.DispatchAsync(operationId, targetStationId, cancellationToken);
            task = await repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
            if (response.State == "failed")
            {
                await repository.ApplyEventAsync(taskId, TaskEvent.DeviceFailed, response, cancellationToken);
            }
            else if (task.Status == DomainTaskStatus.Dispatching)
            {
                await repository.ApplyEventAsync(taskId, started, response, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            await repository.ApplyEventAsync(taskId, TaskEvent.Timeout, new { error = exception.Message }, cancellationToken);
        }
    }

    public static TaskResponse ToResponse(TransportTask task) => new(task.Id, task.SourceStationCode, task.TargetStationCode, task.Status.ToString(), task.RetryCount, task.LastError);
    private static TaskEventResponse ToResponse(TaskEventRecord taskEvent) => new(taskEvent.Id, taskEvent.EventType, taskEvent.Payload, taskEvent.CreatedAt);
}

public sealed class UnsupportedRouteException : InvalidOperationException
{
    public UnsupportedRouteException() : base("MVP only supports SAMPLE_01 to ST_PREP_01.") { }
}
