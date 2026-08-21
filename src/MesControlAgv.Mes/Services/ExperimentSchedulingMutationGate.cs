namespace MesControlAgv.Mes.Services;

/// <summary>
/// Serializes experiment-planning mutations for the single MES process that
/// owns the SQLite store. This keeps request-id replay and reservation checks
/// atomic within the process. Runtime lease exclusion remains database-enforced.
/// </summary>
public sealed class ExperimentSchedulingMutationGate
{
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public Task EnterAsync(CancellationToken cancellationToken) =>
        _mutationGate.WaitAsync(cancellationToken);

    public void Exit() => _mutationGate.Release();
}
