using System.Net.Http;

namespace MesControlAgv.Wpf.Services;

/// <summary>
/// Applies short timeouts to ordinary MES reads without truncating the
/// controller program-catalog scan or a state-changing request.  A single
/// global HttpClient timeout cannot satisfy all three cases safely.
/// </summary>
internal sealed class MesHttpTimeoutHandler : DelegatingHandler
{
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _catalogTimeout;
    private readonly TimeSpan _writeTimeout;

    public MesHttpTimeoutHandler(
        TimeSpan? readTimeout = null,
        TimeSpan? catalogTimeout = null,
        TimeSpan? writeTimeout = null,
        HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        _readTimeout = Validate(readTimeout ?? TimeSpan.FromSeconds(5), nameof(readTimeout));
        _catalogTimeout = Validate(catalogTimeout ?? TimeSpan.FromSeconds(30), nameof(catalogTimeout));
        _writeTimeout = Validate(writeTimeout ?? TimeSpan.FromSeconds(90), nameof(writeTimeout));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timeout = SelectTimeout(request);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await base.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"MES {request.Method} {request.RequestUri?.AbsolutePath ?? "/"} exceeded {timeout.TotalSeconds:0.#} seconds.",
                exception);
        }
    }

    private TimeSpan SelectTimeout(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            return _writeTimeout;

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        return path.EndsWith("/programs", StringComparison.OrdinalIgnoreCase)
            ? _catalogTimeout
            : _readTimeout;
    }

    private static TimeSpan Validate(TimeSpan value, string name) =>
        value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan
            ? throw new ArgumentOutOfRangeException(name, value, "A finite positive timeout is required.")
            : value;
}
