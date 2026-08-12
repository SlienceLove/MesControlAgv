namespace MesControlAgv.Adapter.Services;

/// <summary>
/// Coordinates physical dispatch and explicit control release transactions.
/// Register this type as a singleton so scoped AdapterService instances share
/// the same device session boundary.
/// </summary>
public sealed class PhysicalAgvSessionGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken admissionToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(admissionToken);
        try
        {
            return await operation();
        }
        finally
        {
            _gate.Release();
        }
    }
}
