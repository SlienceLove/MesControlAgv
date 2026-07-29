using MesControlAgv.Domain;
using MesControlAgv.Mes.Contracts;
using MesControlAgv.Mes.Entities;

namespace MesControlAgv.Mes.Services;

public sealed class TaskService(TaskRepository repository)
{
    public async Task<TaskResponse> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken)
    {
        if (request is not { SourceStationCode: 2, TargetStationCode: 4 })
        {
            throw new UnsupportedRouteException();
        }

        return ToResponse(await repository.CreateAsync(
            request.SourceStationCode,
            request.TargetStationCode,
            cancellationToken));
    }

    public async Task<TaskDetailResponse?> GetDetailAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await repository.GetAsync(taskId, cancellationToken);
        if (task is null)
        {
            return null;
        }

        var events = await repository.GetEventsAsync(taskId, cancellationToken);
        return new TaskDetailResponse(ToResponse(task), events.Select(ToResponse).ToList());
    }

    public async Task<IReadOnlyList<TaskResponse>> ListAsync(CancellationToken cancellationToken) =>
        (await repository.ListAsync(cancellationToken)).Select(ToResponse).ToList();

    public static TaskResponse ToResponse(TransportTask task) => new(
        task.Id,
        task.SourceStationCode,
        task.TargetStationCode,
        task.Status.ToString(),
        task.RetryCount,
        task.LastError);

    private static TaskEventResponse ToResponse(TaskEventRecord taskEvent) => new(
        taskEvent.Id,
        taskEvent.EventType,
        taskEvent.Payload,
        taskEvent.CreatedAt);
}

public sealed class UnsupportedRouteException : InvalidOperationException
{
    public UnsupportedRouteException()
        : base("MVP only supports SAMPLE_01 to ST_PREP_01.")
    {
    }
}
