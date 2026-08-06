using System.Net;
using MesControlAgv.Application;
using MesControlAgv.Domain;
using MesControlAgv.Contracts;
using MesControlAgv.Mes.Entities;
using DomainTaskStatus = MesControlAgv.Domain.TaskStatus;
using MesControlAgv.Domain.Profiles;

namespace MesControlAgv.Mes.Services;

public sealed class TaskService : ITaskApplicationService
{
    private readonly TaskRepository _repository;
    private readonly IAgvGateway _adapter;
    private readonly PathPlanner _planner;
    private readonly IReadOnlyDictionary<int, Station> _stations;

    public TaskService(TaskRepository repository, IAgvGateway adapter)
        : this(repository, adapter, ProfileConfiguration.Default, new PathPlanner(AgvMap.Default))
    {
    }

    public TaskService(
        TaskRepository repository,
        IAgvGateway adapter,
        ProfileConfiguration profile,
        PathPlanner planner)
    {
        _repository = repository;
        _adapter = adapter;
        _planner = planner;
        _stations = Stations.FromProfile(profile).ToDictionary(station => station.Code);
    }

    public async Task<TaskResponse> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken)
    {
        var source = GetEnabledStation(request.SourceStationCode);
        var target = GetEnabledStation(request.TargetStationCode);
        try
        {
            _planner.Plan(source.AgvStationId, target.AgvStationId);
        }
        catch (KeyNotFoundException exception)
        {
            throw new UnsupportedRouteException(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            throw new UnsupportedRouteException(exception.Message);
        }

        var task = await _repository.CreateAsync(request.SourceStationCode, request.TargetStationCode, request.Priority, request.Description, request.ExternalId, cancellationToken);
        return ToResponse(task);
    }

    public async Task<TaskResponse> DispatchAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await _repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
        if (task.Status != DomainTaskStatus.Created)
        {
            throw new InvalidTaskTransitionException(task.Status, TaskEvent.DispatchRequested);
        }

        await DispatchLegAsync(task.Id, TaskEvent.PickupMoveStarted, GetEnabledStation(task.SourceStationCode).AgvStationId, cancellationToken);
        return ToResponse((await _repository.GetAsync(task.Id, cancellationToken))!);
    }

    public async Task<TaskResponse> RecordArrivalAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await _repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
        var arrival = task.Status == DomainTaskStatus.MovingToPickup ? TaskEvent.PickupArrived : TaskEvent.DropoffArrived;
        return ToResponse(await _repository.ApplyEventAsync(taskId, arrival, new { source = "adapter" }, cancellationToken));
    }

    public async Task<TaskResponse> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken)
    {
        await _repository.ApplyEventAsync(taskId, TaskEvent.PickupConfirmed, new { operatorName }, cancellationToken);
        var task = await _repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
        await DispatchLegAsync(task.Id, TaskEvent.DropoffMoveStarted, GetEnabledStation(task.TargetStationCode).AgvStationId, cancellationToken);
        return ToResponse((await _repository.GetAsync(task.Id, cancellationToken))!);
    }

    public async Task<TaskResponse> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken) =>
        ToResponse(await _repository.ApplyEventAsync(taskId, TaskEvent.DropoffConfirmed, new { operatorName }, cancellationToken));

    public async Task<TaskResponse> RetryAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var current = await _repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
        if (current.Status != DomainTaskStatus.Failed)
        {
            throw new InvalidTaskTransitionException(current.Status, TaskEvent.RetryRequested);
        }
        var task = await _repository.IncrementRetryAsync(taskId, cancellationToken);
        await _repository.ApplyEventAsync(taskId, TaskEvent.RetryRequested, new { retry = task.RetryCount }, cancellationToken);
        var target = task.ActiveTargetStationId ?? throw new InvalidOperationException("Task has no target station.");
        var eventType = target == GetEnabledStation(task.SourceStationCode).AgvStationId ? TaskEvent.PickupMoveStarted : TaskEvent.DropoffMoveStarted;
        await DispatchLegAsync(taskId, eventType, target, cancellationToken);
        return ToResponse((await _repository.GetAsync(taskId, cancellationToken))!);
    }

    public async Task<TaskResponse> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken)
    {
        var task = await _repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
        if (task.Status == DomainTaskStatus.Created)
        {
            return ToResponse(await _repository.ApplyEventAsync(
                taskId,
                TaskEvent.CancelConfirmed,
                new { operatorName, source = "mes-pending-task" },
                cancellationToken));
        }

        var operationId = task.ActiveTargetStationId == GetEnabledStation(task.TargetStationCode).AgvStationId
            ? TransportOperationIds.Dropoff(task.Id)
            : TransportOperationIds.Pickup(task.Id);
        var cancellation = await _adapter.CancelAsync(operationId, cancellationToken);
        if (cancellation?.State == "cancelled")
            return ToResponse(await _repository.ApplyEventAsync(taskId, TaskEvent.CancelConfirmed, new { operatorName }, cancellationToken));

        if (cancellation is { State: "unknown" }
            && task.Status is DomainTaskStatus.Dispatching or DomainTaskStatus.MovingToPickup or DomainTaskStatus.MovingToDropoff or DomainTaskStatus.Paused)
        {
            var error = cancellation.LastError ?? "cancel_not_confirmed_by_1110";
            return ToResponse(await _repository.ApplyEventAsync(
                taskId,
                TaskEvent.Timeout,
                new { operatorName, operationId, source = "adapter-cancel", error },
                cancellationToken,
                error));
        }

        throw new InvalidOperationException("Adapter did not confirm cancellation.");
    }

    public async Task<TaskResponse> MarkUnknownAsync(Guid taskId, CancellationToken cancellationToken) =>
        ToResponse(await _repository.ApplyEventAsync(taskId, TaskEvent.Timeout, new { source = "recovery" }, cancellationToken));

    public async Task<TaskResponse> RecoverAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await _repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
        if (task.Status != DomainTaskStatus.Unknown) return ToResponse(task);
        var pickup = task.ActiveTargetStationId == GetEnabledStation(task.SourceStationCode).AgvStationId;
        var operationId = pickup ? TransportOperationIds.Pickup(task.Id) : TransportOperationIds.Dropoff(task.Id);
        var device = await _adapter.GetTaskAsync(operationId, cancellationToken);
        if (device is not null)
        {
            await _repository.SetActiveRouteAsync(
                taskId,
                task.ActiveTargetStationId ?? string.Empty,
                device.AgvId,
                device.DeviceTaskId,
                device.Path,
                cancellationToken);
        }
        var reconciliation = device?.State switch
        {
            "accepted" or "moving" => pickup ? TaskEvent.ReconciledMoving : TaskEvent.ReconciledMovingToDropoff,
            "arrived" => pickup ? TaskEvent.ReconciledPickupArrived : TaskEvent.ReconciledDropoffArrived,
            "completed" => TaskEvent.ReconciledCompleted,
            "failed" => TaskEvent.ReconciledFailed,
            "cancelled" => TaskEvent.CancelConfirmed,
            _ => throw new InvalidOperationException("Device task cannot be reconciled.")
        };
        return ToResponse(await _repository.ApplyEventAsync(taskId, reconciliation, new { deviceState = device?.State }, cancellationToken));
    }

    public async Task<TaskResponse?> RecordAgvCommandAsync(
        Guid operationId,
        string command,
        AgvTaskResponse result,
        CancellationToken cancellationToken)
    {
        var task = await _repository.GetByActiveOperationAsync(operationId, cancellationToken);
        if (task is null) return null;

        var normalizedCommand = command.Trim().ToLowerInvariant();
        var deviceState = result.State.Trim().ToLowerInvariant();
        if (normalizedCommand == "pause" && deviceState == "paused" && task.Status == DomainTaskStatus.Paused)
        {
            return ToResponse(task);
        }

        if ((normalizedCommand is "resume" or "continue") && (deviceState is "accepted" or "moving"))
        {
            var resumedStatus = IsDropoffLeg(task)
                ? DomainTaskStatus.MovingToDropoff
                : DomainTaskStatus.MovingToPickup;
            if (task.Status == resumedStatus)
            {
                return ToResponse(task);
            }
        }

        TaskEvent taskEvent = normalizedCommand switch
        {
            "pause" when deviceState == "paused" => TaskEvent.PauseRequested,
            "resume" or "continue" when deviceState is "accepted" or "moving" =>
                IsDropoffLeg(task) ? TaskEvent.ResumeDropoffRequested : TaskEvent.ResumeRequested,
            _ => throw new InvalidOperationException(
                $"Adapter did not confirm {normalizedCommand} for operation {operationId:N} (state: {result.State}).")
        };

        var updated = await _repository.ApplyEventAsync(
            task.Id,
            taskEvent,
            new { operationId, command = normalizedCommand, deviceState, result.DeviceTaskId, source = "adapter-command" },
            cancellationToken);
        return ToResponse(updated);
    }

    public async Task<IReadOnlyList<AgvFleetStatusResponse>> GetFleetStatusAsync(CancellationToken cancellationToken)
    {
        var snapshots = _adapter is IFleetAwareAgvGateway fleet
            ? await fleet.GetFleetSnapshotAsync(cancellationToken)
            : [await _adapter.GetSnapshotAsync(cancellationToken)];
        var activeTasks = await _repository.ListActiveAssignedAsync(cancellationToken);

        var results = new List<AgvFleetStatusResponse>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            var task = activeTasks.FirstOrDefault(candidate =>
                StringComparer.Ordinal.Equals(candidate.ActiveAgvId, snapshot.AgvId));
            if (task is null)
            {
                results.Add(new AgvFleetStatusResponse(snapshot, null));
                continue;
            }

            var operationId = GetActiveOperationId(task);
            AgvTaskResponse? device = null;
            string? deviceReadError = null;
            try
            {
                device = await _adapter.GetTaskAsync(operationId, cancellationToken);
            }
            catch (AdapterHttpException exception)
            {
                deviceReadError = exception.Detail ?? exception.Message;
            }
            catch (HttpRequestException exception)
            {
                deviceReadError = exception.Message;
            }
            catch (TimeoutException exception)
            {
                deviceReadError = exception.Message;
            }
            results.Add(new AgvFleetStatusResponse(
                snapshot,
                new AgvActiveTaskStatusResponse(
                    task.Id,
                    operationId,
                    task.Status.ToString(),
                    device?.DeviceTaskId ?? task.ActiveDeviceTaskId,
                    device?.State,
                    task.ActiveTargetStationId,
                    device?.LastError ?? deviceReadError ?? task.LastError,
                    DeserializePath(task.ActivePathJson))));
        }

        return results;
    }

    public async Task ReconcileIncompleteAsync(CancellationToken cancellationToken)
    {
        var unknownTasks = await _repository.ListByStatusAsync(DomainTaskStatus.Unknown, cancellationToken);
        foreach (var status in new[] { DomainTaskStatus.Dispatching, DomainTaskStatus.MovingToPickup, DomainTaskStatus.MovingToDropoff })
        {
            var tasks = await _repository.ListByStatusAsync(status, cancellationToken);
            foreach (var task in tasks)
            {
                await _repository.ApplyEventAsync(task.Id, TaskEvent.Timeout, new { source = "startup-recovery" }, cancellationToken);
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

    public async Task ReconcileActiveAsync(CancellationToken cancellationToken)
    {
        foreach (var status in new[] { DomainTaskStatus.Dispatching, DomainTaskStatus.MovingToPickup, DomainTaskStatus.MovingToDropoff, DomainTaskStatus.Paused })
        {
            var tasks = await _repository.ListByStatusAsync(status, cancellationToken);
            foreach (var task in tasks)
            {
                try
                {
                    await ReconcileActiveTaskAsync(task.Id, cancellationToken);
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

        var unknownTasks = await _repository.ListByStatusAsync(DomainTaskStatus.Unknown, cancellationToken);
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

    private async Task ReconcileActiveTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await _repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
        if (task.Status is not (DomainTaskStatus.Dispatching or DomainTaskStatus.MovingToPickup or DomainTaskStatus.MovingToDropoff or DomainTaskStatus.Paused)) return;

        var pickup = task.ActiveTargetStationId == GetEnabledStation(task.SourceStationCode).AgvStationId;
        var operationId = pickup ? TransportOperationIds.Pickup(task.Id) : TransportOperationIds.Dropoff(task.Id);
        var device = await _adapter.GetTaskAsync(operationId, cancellationToken);
        if (device is null) return;
        await _repository.SetActiveRouteAsync(
            taskId,
            task.ActiveTargetStationId ?? string.Empty,
            device.AgvId,
            device.DeviceTaskId,
            device.Path,
            cancellationToken);

        var state = device.State?.Trim().ToLowerInvariant();
        if (state == "paused")
        {
            if (task.Status is DomainTaskStatus.MovingToPickup or DomainTaskStatus.MovingToDropoff)
            {
                await _repository.ApplyEventAsync(
                    taskId,
                    TaskEvent.PauseRequested,
                    new { deviceState = state, source = "adapter-poll" },
                    cancellationToken);
            }
            return;
        }

        if (state is "accepted" or "moving")
        {
            if (task.Status == DomainTaskStatus.Dispatching)
            {
                await _repository.ApplyEventAsync(
                    taskId,
                    pickup ? TaskEvent.PickupMoveStarted : TaskEvent.DropoffMoveStarted,
                    new { deviceState = state, source = "adapter-poll" },
                    cancellationToken);
            }
            else if (task.Status == DomainTaskStatus.Paused)
            {
                await _repository.ApplyEventAsync(
                    taskId,
                    pickup ? TaskEvent.ResumeRequested : TaskEvent.ResumeDropoffRequested,
                    new { deviceState = state, source = "adapter-poll" },
                    cancellationToken);
            }
            return;
        }

        if (state is "arrived" or "completed")
        {
            if (task.Status == DomainTaskStatus.Dispatching)
            {
                await _repository.ApplyEventAsync(
                    taskId,
                    pickup ? TaskEvent.PickupMoveStarted : TaskEvent.DropoffMoveStarted,
                    new { deviceState = state, source = "adapter-poll" },
                    cancellationToken);
            }

            await _repository.ApplyEventAsync(
                taskId,
                pickup ? TaskEvent.PickupArrived : TaskEvent.DropoffArrived,
                new { deviceState = state, source = "adapter-poll" },
                cancellationToken);
            return;
        }

        if (state == "failed")
        {
            await _repository.ApplyEventAsync(
                taskId,
                TaskEvent.DeviceFailed,
                new { deviceState = state, source = "adapter-poll", error = device.LastError },
                cancellationToken,
                device.LastError);
            return;
        }

        if (state == "cancelled")
        {
            await _repository.ApplyEventAsync(
                taskId,
                TaskEvent.CancelConfirmed,
                new { deviceState = state, source = "adapter-poll" },
                cancellationToken);
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
        var task = await _repository.GetAsync(taskId, cancellationToken);
        if (task is null) return null;
        var events = await _repository.GetEventsAsync(taskId, cancellationToken);
        return new TaskDetailResponse(ToResponse(task), events.Select(ToResponse).ToList());
    }

    public async Task<IReadOnlyList<TaskResponse>> ListAsync(DateOnly date, CancellationToken cancellationToken) =>
        (await _repository.ListAsync(date, cancellationToken)).Select(ToResponse).ToList();

    public Task<IReadOnlyList<TaskResponse>> ListAsync(CancellationToken cancellationToken) =>
        ListAsync(DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken);

    private async Task DispatchLegAsync(Guid taskId, TaskEvent started, string targetStationId, CancellationToken cancellationToken)
    {
        var task = await _repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
        var operationId = started == TaskEvent.PickupMoveStarted ? TransportOperationIds.Pickup(taskId) : TransportOperationIds.Dropoff(taskId);
        try
        {
            var sourceStationId = GetEnabledStation(task.SourceStationCode).AgvStationId;
            var navigationSourceStationId = sourceStationId;
            IReadOnlyList<string>? plannedPath = null;
            var plannedCost = 0d;
            var preDispatchSnapshot = await _adapter.GetSnapshotAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(preDispatchSnapshot.CurrentStationId))
            {
                var planned = _planner.PlanVia(
                    preDispatchSnapshot.CurrentStationId,
                    sourceStationId,
                    targetStationId);
                navigationSourceStationId = planned.Start;
                plannedPath = planned.Stations;
                plannedCost = planned.Cost;
            }
            else
            {
                var planned = _planner.Plan(sourceStationId, targetStationId);
                plannedPath = planned.Stations;
                plannedCost = planned.Cost;
            }

            await _repository.RecordEventAsync(
                taskId,
                "PathPlanned",
                new
                {
                    source = "mes-pre-dispatch",
                    agvId = preDispatchSnapshot.AgvId,
                    currentStationId = preDispatchSnapshot.CurrentStationId,
                    targetStationId,
                    path = plannedPath,
                    cost = plannedCost,
                    observedAtUtc = DateTime.UtcNow
                },
                cancellationToken);

            if (task.Status == DomainTaskStatus.Created)
            {
                await _repository.ApplyEventAsync(
                    taskId,
                    TaskEvent.DispatchRequested,
                    new { targetStationId, plannedPath, source = "mes-pre-dispatch" },
                    cancellationToken);
            }
            await _repository.SetActiveTargetAsync(taskId, targetStationId, cancellationToken);

            if (!string.IsNullOrWhiteSpace(preDispatchSnapshot.CurrentStationId)
                && plannedPath is { Count: 1 }
                && StringComparer.Ordinal.Equals(plannedPath[0], targetStationId))
            {
                await _repository.SetActiveRouteAsync(
                    taskId,
                    targetStationId,
                    preDispatchSnapshot.AgvId,
                    null,
                    plannedPath,
                    cancellationToken);
                task = await _repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
                if (task.Status == DomainTaskStatus.Dispatching)
                {
                    await _repository.ApplyEventAsync(
                        taskId,
                        started,
                        new { source = "mes-pre-dispatch-plan", path = plannedPath },
                        cancellationToken);
                }
                await _repository.ApplyEventAsync(
                    taskId,
                    started == TaskEvent.PickupMoveStarted ? TaskEvent.PickupArrived : TaskEvent.DropoffArrived,
                    new { source = "mes-pre-dispatch-plan", path = plannedPath },
                    cancellationToken);
                return;
            }

            var response = _adapter is IPathAwareAgvGateway pathAwareGateway
                && plannedPath is { Count: >= 2 }
                && !string.IsNullOrWhiteSpace(preDispatchSnapshot.CurrentStationId)
                ? await pathAwareGateway.DispatchAsync(
                    operationId,
                    navigationSourceStationId,
                    targetStationId,
                    plannedPath,
                    cancellationToken)
                : _adapter is IRouteAwareAgvGateway routeAware
                    ? await routeAware.DispatchAsync(operationId, navigationSourceStationId, targetStationId, cancellationToken)
                    : await _adapter.DispatchAsync(operationId, targetStationId, cancellationToken);
            await _repository.SetActiveRouteAsync(
                taskId,
                targetStationId,
                response.AgvId,
                response.DeviceTaskId,
                response.Path ?? plannedPath,
                cancellationToken);
            task = await _repository.GetAsync(taskId, cancellationToken) ?? throw new KeyNotFoundException();
            if (response.State == "failed")
            {
                await _repository.ApplyEventAsync(taskId, TaskEvent.DeviceFailed, response, cancellationToken, response.LastError);
            }
            else if (task.Status == DomainTaskStatus.Dispatching)
            {
                await _repository.ApplyEventAsync(taskId, started, response, cancellationToken);
            }
        }
        catch (AdapterHttpException exception) when (exception.ResponseStatusCode == HttpStatusCode.Conflict)
        {
            var error = DescribeAdapterConflict(exception);
            await _repository.ApplyEventAsync(
                taskId,
                TaskEvent.DeviceFailed,
                new { source = "adapter", statusCode = (int)exception.ResponseStatusCode, reason = error, detail = exception.Detail },
                cancellationToken,
                error);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var error = DescribeSystemFailure(exception);
            await _repository.ApplyEventAsync(taskId, TaskEvent.Timeout, new { error }, cancellationToken, error);
        }
        catch (TimeoutException exception)
        {
            var error = DescribeSystemFailure(exception);
            await _repository.ApplyEventAsync(taskId, TaskEvent.Timeout, new { error }, cancellationToken, error);
        }
        catch (HttpRequestException exception)
        {
            var error = DescribeSystemFailure(exception);
            await _repository.ApplyEventAsync(taskId, TaskEvent.Timeout, new { error }, cancellationToken, error);
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

    private bool IsDropoffLeg(TransportTask task) =>
        string.Equals(
            task.ActiveTargetStationId,
            GetEnabledStation(task.TargetStationCode).AgvStationId,
            StringComparison.Ordinal);

    private Guid GetActiveOperationId(TransportTask task) =>
        IsDropoffLeg(task) ? TransportOperationIds.Dropoff(task.Id) : TransportOperationIds.Pickup(task.Id);

    private Station GetEnabledStation(int code)
    {
        if (!_stations.TryGetValue(code, out var station) || !station.Enabled)
        {
            throw new UnsupportedRouteException($"Station code {code} is not configured or enabled in the active profile.");
        }

        return station;
    }

    private TaskResponse ToResponse(TransportTask task) => new(
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
        task.EndedAt,
        task.ActiveAgvId,
        task.ActiveDeviceTaskId,
        DeserializePath(task.ActivePathJson));
    private static TaskEventResponse ToResponse(TaskEventRecord taskEvent) => new(taskEvent.Id, taskEvent.EventType, taskEvent.Payload, taskEvent.CreatedAt);

    private static IReadOnlyList<string>? DeserializePath(string? pathJson) =>
        pathJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<string>>(pathJson);
}

public sealed class UnsupportedRouteException : InvalidOperationException
{
    public UnsupportedRouteException() : base("MVP only supports SAMPLE_01 to ST_PREP_01.") { }
    public UnsupportedRouteException(string message) : base(message) { }
}


