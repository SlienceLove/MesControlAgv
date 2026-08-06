using MesControlAgv.Contracts;

namespace MesControlAgv.Application;

/// <summary>
/// Server-side application boundary for task lifecycle use cases.
/// Implementations may use persistence and device gateways, but callers do not depend on either detail.
/// </summary>
public interface ITaskApplicationService
{
    Task<TaskResponse> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken);
    Task<TaskResponse> DispatchAsync(Guid taskId, CancellationToken cancellationToken);
    Task<TaskResponse> RecordArrivalAsync(Guid taskId, CancellationToken cancellationToken);
    Task<TaskResponse> ConfirmPickupAsync(Guid taskId, string operatorName, CancellationToken cancellationToken);
    Task<TaskResponse> ConfirmDropoffAsync(Guid taskId, string operatorName, CancellationToken cancellationToken);
    Task<TaskResponse> RetryAsync(Guid taskId, CancellationToken cancellationToken);
    Task<TaskResponse> CancelAsync(Guid taskId, string operatorName, CancellationToken cancellationToken);
    Task<TaskResponse> RecoverAsync(Guid taskId, CancellationToken cancellationToken);
    Task<TaskResponse?> RecordAgvCommandAsync(Guid operationId, string command, AgvTaskResponse result, CancellationToken cancellationToken);
    Task ReconcileIncompleteAsync(CancellationToken cancellationToken);
    Task ReconcileActiveAsync(CancellationToken cancellationToken);
    Task<TaskDetailResponse?> GetDetailAsync(Guid taskId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskResponse>> ListAsync(DateOnly date, CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskResponse>> ListAsync(CancellationToken cancellationToken);
}
