namespace MesControlAgv.Wpf.DigitalTwin;

/// <summary>Three independent read loops, scoped to one visible viewer lifetime.</summary>
public sealed class DigitalTwinStatusSession : IDisposable
{
    private readonly IDigitalTwinStatusSource _source;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _stop = new();
    private bool _started;
    private bool _disposed;
    public event Action<TwinReading>? Reading;
    public Task Completion { get; private set; } = Task.CompletedTask;

    public DigitalTwinStatusSession(IDigitalTwinStatusSource source, TimeSpan? interval = null, TimeSpan? timeout = null)
    {
        _source = source;
        _interval = interval ?? TimeSpan.FromSeconds(2);
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
        if (_interval <= TimeSpan.Zero || _timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;
        Completion = Task.WhenAll(Enum.GetValues<TwinChannel>().Select(PollAsync));
    }

    private async Task PollAsync(TwinChannel channel)
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
                request.CancelAfter(_timeout);
                Task<TwinReading>? pending = null;
                try
                {
                    pending = _source.ReadAsync(channel, request.Token);
                    var reading = await pending.WaitAsync(request.Token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested) Reading?.Invoke(reading);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    if (!token.IsCancellationRequested)
                        Reading?.Invoke(TwinProjection.Failed(channel, _source.Bindings.Id(channel),
                            error is OperationCanceledException or TimeoutException ? "读取超时，未将设备判为离线。" : "MES 读取不可用，请检查服务连接。"));
                    // Do not launch another request while a non-cooperative source is still completing.
                    if (pending is { IsCompleted: false })
                    {
                        try { await pending.WaitAsync(token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                        catch (Exception completionError) when (completionError is not OutOfMemoryException) { }
                    }
                }
                finally
                {
                    // Observe a fault that arrives after page cancellation without retaining the page.
                    if (pending is { IsCompleted: false })
                        _ = pending.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                await Task.Delay(_interval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        Reading = null;
        // Release the CTS only after the loops no longer access its token.
        _ = Completion.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
