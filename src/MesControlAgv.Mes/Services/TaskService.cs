using System.Net;
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
        var task = await repository.CreateAsync(request.SourceStationCode, request.TargetStationCode, request.Priority, request.Description, request.ExternalId, cancellationToken);
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
        var current = await repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
        if (current.Status != DomainTaskStatus.Failed)
        {
            throw new InvalidTaskTransitionException(current.Status, TaskEvent.RetryRequested);
        }
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
            "accepted" or "moving" => pickup ? TaskEvent.ReconciledMoving : TaskEvent.ReconciledMovingToDropoff,
            "arrived" => pickup ? TaskEvent.ReconciledPickupArrived : TaskEvent.ReconciledDropoffArrived,
            "completed" => TaskEvent.ReconciledCompleted,
            "failed" => TaskEvent.ReconciledFailed,
            _ => throw new InvalidOperationException("Device task cannot be reconciled.")
        };
        return ToResponse(await repository.ApplyEventAsync(taskId, reconciliation, new { deviceState = device?.State }, cancellationToken));
    }

    public async Task ReconcileIncompleteAsync(CancellationToken cancellationToken)
    {
        var unknownTasks = await repository.ListByStatusAsync(DomainTaskStatus.Unknown, cancellationToken);
        foreach (var status in new[] { DomainTaskStatus.Dispatching, DomainTaskStatus.MovingToPickup, DomainTaskStatus.MovingToDropoff })
        {
            var tasks = await repository.ListByStatusAsync(status, cancellationToken);
            foreach (var task in tasks)
            {
                await repository.ApplyEventAsync(task.Id, TaskEvent.Timeout, new { source = "startup-recovery" }, cancellationToken);
                try
                {
                    await RecoverWithRetryAsync(task.Id, cancellationToken);
                }
                catch (InvalidOperationException)
                {
                }
                catch (HttpRequestException)
                {
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }
        }

        foreach (var task in unknownTasks)
        {
            try
            {
                await RecoverWithRetryAsync(task.Id, cancellationToken);
            }
            catch (InvalidOperationException)
            {
            }
            catch (HttpRequestException)
            {
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private static readonly TimeSpan[] RecoveryBackoff =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100)
    ];

    private async Task<TaskResponse> RecoverWithRetryAsync(Guid taskId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await RecoverAsync(taskId, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < RecoveryBackoff.Length)
            {
                await Task.Delay(RecoveryBackoff[attempt], cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < RecoveryBackoff.Length)
            {
                await Task.Delay(RecoveryBackoff[attempt], cancellationToken);
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

    public async Task<IReadOnlyList<TaskResponse>> ListAsync(DateOnly date, CancellationToken cancellationToken) =>
        (await repository.ListAsync(date, cancellationToken)).Select(ToResponse).ToList();

    public Task<IReadOnlyList<TaskResponse>> ListAsync(CancellationToken cancellationToken) =>
        ListAsync(DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken);

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
            var sourceStationId = Stations.Get(task.SourceStationCode).AgvStationId;
            var response = adapter is IRouteAwareAdapterClient routeAware
                ? await routeAware.DispatchAsync(operationId, sourceStationId, targetStationId, cancellationToken)
                : await adapter.DispatchAsync(operationId, targetStationId, cancellationToken);
            task = await repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
            if (response.State == "failed")
            {
                await repository.ApplyEventAsync(taskId, TaskEvent.DeviceFailed, response, cancellationToken, response.LastError);
            }
            else if (task.Status == DomainTaskStatus.Dispatching)
            {
                await repository.ApplyEventAsync(taskId, started, response, cancellationToken);
            }
        }
        catch (AdapterHttpException exception) when (exception.ResponseStatusCode == HttpStatusCode.Conflict)
        {
            var error = DescribeAdapterConflict(exception);
            await repository.ApplyEventAsync(
                taskId,
                TaskEvent.DeviceFailed,
                new { source = "adapter", statusCode = (int)exception.ResponseStatusCode, reason = error, detail = exception.Detail },
                cancellationToken,
                error);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var error = DescribeSystemFailure(exception);
            await repository.ApplyEventAsync(taskId, TaskEvent.Timeout, new { error }, cancellationToken, error);
        }
        catch (TimeoutException exception)
        {
            var error = DescribeSystemFailure(exception);
            await repository.ApplyEventAsync(taskId, TaskEvent.Timeout, new { error }, cancellationToken, error);
        }
        catch (HttpRequestException exception)
        {
            var error = DescribeSystemFailure(exception);
            await repository.ApplyEventAsync(taskId, TaskEvent.Timeout, new { error }, cancellationToken, error);
        }
    }

    private static string DescribeAdapterConflict(AdapterHttpException exception)
    {
        return exception.Detail switch
        {
            "No online, idle AGV controlled by adapter is available." => "\u6CA1\u6709\u53EF\u7528\u7684\u7A7A\u95F2 AGV\u3002",
            "All available AGVs are blocked by active route reservations." => "\u6240\u6709\u53EF\u7528 AGV \u7684\u8DEF\u5F84\u90FD\u88AB\u5F53\u524D\u4EFB\u52A1\u5360\u7528\u3002",
            { } detail when detail.StartsWith("AGV control owner", StringComparison.Ordinal) => $"AGV \u63A7\u5236\u6743\u4E0D\u53EF\u7528\uFF1A{detail}",
            { } detail when !string.IsNullOrWhiteSpace(detail) => $"AGV \u6682\u65F6\u65E0\u6CD5\u63A5\u6536\u4EFB\u52A1\uFF1A{detail}",
            _ => "AGV \u6682\u65F6\u65E0\u6CD5\u63A5\u6536\u4EFB\u52A1\uFF08Adapter \u8FD4\u56DE 409 \u51B2\u7A81\uFF09\u3002"
        };
    }

    private static string DescribeSystemFailure(Exception exception) => exception switch
    {
        AdapterHttpException adapter => string.IsNullOrWhiteSpace(adapter.Detail)
            ? $"Adapter \u901A\u4FE1\u5F02\u5E38\uFF08HTTP {(int)adapter.ResponseStatusCode}\uFF09\u3002"
            : $"Adapter \u901A\u4FE1\u5F02\u5E38\uFF1A{adapter.Detail}",
        TimeoutException => "AGV \u54CD\u5E94\u8D85\u65F6\uFF0C\u6682\u65F6\u65E0\u6CD5\u786E\u8BA4\u8BBE\u5907\u72B6\u6001\u3002",
        TaskCanceledException => "AGV \u8BF7\u6C42\u8D85\u65F6\u6216\u88AB\u53D6\u6D88\uFF0C\u6682\u65F6\u65E0\u6CD5\u786E\u8BA4\u8BBE\u5907\u72B6\u6001\u3002",
        HttpRequestException => $"Adapter \u901A\u4FE1\u5931\u8D25\uFF1A{exception.Message}",
        _ => $"\u7CFB\u7EDF\u5F02\u5E38\uFF1A{exception.Message}"
    };

    public static TaskResponse ToResponse(TransportTask task) => new(
        task.Id,
        task.SourceStationCode,
        task.TargetStationCode,
        task.Status.ToString(),
        task.RetryCount,
        task.LastError,
        task.Priority,
        task.Description,
        task.ExternalId,
        task.CreatedAt,
        task.EndedAt);
    private static TaskEventResponse ToResponse(TaskEventRecord taskEvent) => new(taskEvent.Id, taskEvent.EventType, taskEvent.Payload, taskEvent.CreatedAt);
}

public sealed class UnsupportedRouteException : InvalidOperationException
{
    public UnsupportedRouteException() : base("MVP only supports SAMPLE_01 to ST_PREP_01.") { }
}
