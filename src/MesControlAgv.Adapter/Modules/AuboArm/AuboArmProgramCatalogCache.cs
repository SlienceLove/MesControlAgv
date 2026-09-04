using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Modules.AuboArm;

/// <summary>
/// Process-local cache for the read-only AUBO program catalog.
///
/// A catalog read is deliberately expensive because the controller exposes
/// individual preload slots rather than a directory endpoint.  This cache makes
/// ordinary UI refreshes cheap while keeping an explicit fresh read available for
/// preflight and acceptance evidence.  Concurrent callers for one device share a
/// single scan; an incomplete or offline response is never stored as a successful
/// cache entry.
/// </summary>
public sealed class AuboArmProgramCatalogCache
{
    private sealed record CacheEntry(
        AuboArmProgramCatalogResponse Response,
        DateTimeOffset ExpiresAtUtc);

    private sealed class InFlight(long generation)
    {
        public long Generation { get; } = generation;

        public TaskCompletionSource<AuboArmProgramCatalogResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InFlight> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _generations = new(StringComparer.OrdinalIgnoreCase);

    public AuboArmProgramCatalogCache(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Returns a valid cached response when allowed, otherwise coalesces callers
    /// onto one fresh scan. The scan callback receives <see cref="CancellationToken.None" />
    /// so cancellation of one HTTP request cannot leave another waiter with a
    /// half-completed catalog; each caller can still cancel its own wait.
    /// </summary>
    public async Task<AuboArmProgramCatalogResponse> GetOrRefreshAsync(
        string deviceId,
        TimeSpan ttl,
        bool forceFresh,
        Func<CancellationToken, Task<AuboArmProgramCatalogResponse>> scan,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(scan);
        ValidateTtl(ttl);
        cancellationToken.ThrowIfCancellationRequested();

        var key = deviceId.Trim();
        InFlight flight;
        var startScan = false;

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var generation = GetGenerationLocked(key);
            if (_inFlight.TryGetValue(key, out flight!)
                && flight.Generation == generation)
            {
                // Join an already-running fresh scan before considering a
                // previously cached value. This prevents an ordinary UI read
                // from returning stale data while an explicit preflight read
                // is in progress.
            }
            else
            {
                if (!forceFresh
                    && ttl > TimeSpan.Zero
                    && _entries.TryGetValue(key, out var entry))
                {
                    if (entry.Response.IsComplete && entry.Response.Online && entry.ExpiresAtUtc > now)
                    {
                        return entry.Response with
                        {
                            IsCached = true,
                            CacheExpiresAtUtc = entry.ExpiresAtUtc
                        };
                    }

                    _entries.Remove(key);
                }

                flight = new InFlight(generation);
                _inFlight[key] = flight;
                startScan = true;
            }
        }

        if (startScan)
            _ = RunScanAsync(key, ttl, flight, scan);

        return await flight.Completion.Task
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Drops both a previously completed entry and any result that an older
    /// in-flight scan might otherwise publish after a mutating operation.
    /// The old read is allowed to finish, but its generation no longer matches
    /// and therefore cannot repopulate the cache.
    /// </summary>
    public void Invalidate(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var key = deviceId.Trim();
        lock (_gate)
        {
            _entries.Remove(key);
            _generations[key] = GetGenerationLocked(key) + 1;
            if (_inFlight.TryGetValue(key, out var flight)
                && flight.Generation != _generations[key])
            {
                _inFlight.Remove(key);
            }
        }
    }

    private async Task RunScanAsync(
        string key,
        TimeSpan ttl,
        InFlight flight,
        Func<CancellationToken, Task<AuboArmProgramCatalogResponse>> scan)
    {
        AuboArmProgramCatalogResponse response;
        try
        {
            response = await scan(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RemoveInFlight(key, flight);
            flight.Completion.TrySetException(exception);
            return;
        }

        var freshResponse = response with
        {
            IsCached = false,
            CacheExpiresAtUtc = null
        };
        DateTimeOffset? expiresAtUtc = null;
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            var generation = GetGenerationLocked(key);
            if (generation == flight.Generation
                && ttl > TimeSpan.Zero
                && freshResponse.IsComplete
                && freshResponse.Online)
            {
                expiresAtUtc = now.Add(ttl);
                _entries[key] = new CacheEntry(freshResponse, expiresAtUtc.Value);
            }

            if (_inFlight.TryGetValue(key, out var current)
                && ReferenceEquals(current, flight))
            {
                _inFlight.Remove(key);
            }
        }

        flight.Completion.TrySetResult(freshResponse with { CacheExpiresAtUtc = expiresAtUtc });
    }

    private void RemoveInFlight(string key, InFlight flight)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(key, out var current)
                && ReferenceEquals(current, flight))
            {
                _inFlight.Remove(key);
            }
        }
    }

    private long GetGenerationLocked(string key) =>
        _generations.TryGetValue(key, out var generation) ? generation : 0;

    private static void ValidateTtl(TimeSpan ttl)
    {
        if (ttl < TimeSpan.Zero || ttl == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                ttl,
                "Catalog cache TTL must be zero or a finite positive duration.");
        }
    }
}
