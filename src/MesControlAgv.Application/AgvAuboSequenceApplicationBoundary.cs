using MesControlAgv.Contracts;

namespace MesControlAgv.Application;

/// <summary>
/// Explicit, operator-triggered continuation of an arrived AGV task with one
/// approved AUBO project.  It never dispatches an AGV and never retries a
/// mutating arm call automatically.
/// </summary>
public interface IAgvAuboSequenceService
{
    Task<AgvAuboSequenceResponse> StartAsync(
        AgvAuboSequenceRequest request,
        CancellationToken cancellationToken);

    Task<AgvAuboSequenceResponse?> GetAsync(
        Guid sequenceId,
        CancellationToken cancellationToken);
}
