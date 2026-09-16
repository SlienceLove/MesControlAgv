namespace MesControlAgv.Mes.Services;

internal static class FieldNavigationAcceptanceGate
{
    internal static readonly SemaphoreSlim Semaphore = new(1, 1);
    internal static async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await Semaphore.WaitAsync(ct);
        try { return await action(); }
        finally { Semaphore.Release(); }
    }
}
