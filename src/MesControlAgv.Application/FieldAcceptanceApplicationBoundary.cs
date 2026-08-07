using MesControlAgv.Contracts;

namespace MesControlAgv.Application;

/// <summary>
/// Isolated use cases for a one-time, physically supervised navigation
/// acceptance. It is intentionally separate from normal transport tasks.
/// </summary>
public interface IFieldNavigationAcceptanceApplicationService
{
    Task<FieldNavigationAcceptanceResponse> CreateAsync(
        CreateFieldNavigationAcceptanceRequest request,
        CancellationToken cancellationToken);

    Task<FieldNavigationAcceptanceResponse> AuthorizeAsync(
        Guid acceptanceId,
        AuthorizeFieldNavigationAcceptanceRequest request,
        CancellationToken cancellationToken);

    Task<FieldNavigationAcceptanceResponse> DispatchAsync(Guid acceptanceId, CancellationToken cancellationToken);

    Task<FieldNavigationAcceptanceResponse> CancelAsync(Guid acceptanceId, CancellationToken cancellationToken);

    Task<FieldNavigationAcceptanceDetailResponse?> GetAsync(Guid acceptanceId, CancellationToken cancellationToken);
}

/// <summary>
/// Optional infrastructure port reserved for the Adapter's dedicated field
/// navigation dispatch endpoint.
/// </summary>
public interface IFieldNavigationAcceptanceGateway : IPhysicalPreflightAgvGateway
{
    Task<AgvTaskResponse> DispatchFieldNavigationAcceptanceAsync(
        Guid acceptanceId,
        FieldNavigationDispatchCommand command,
        CancellationToken cancellationToken);
}
