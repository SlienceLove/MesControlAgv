using MesControlAgv.Adapter.Modules.AuboArm;
using MesControlAgv.Contracts;

namespace MesControlAgv.Adapter.Tests;

public sealed class AuboArmProgramCatalogCacheTests
{
    [Fact]
    public async Task Complete_scan_is_reused_until_ttl_expires()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-04T01:00:00Z"));
        var cache = new AuboArmProgramCatalogCache(clock);
        var calls = 0;

        Task<AuboArmProgramCatalogResponse> Scan(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(CompleteCatalog(clock.GetUtcNow()));
        }

        var first = await cache.GetOrRefreshAsync(
            "ARM-01", TimeSpan.FromMinutes(1), false, Scan, CancellationToken.None);
        var cached = await cache.GetOrRefreshAsync(
            "ARM-01", TimeSpan.FromMinutes(1), false, Scan, CancellationToken.None);

        Assert.False(first.IsCached);
        Assert.True(cached.IsCached);
        Assert.Equal(1, calls);
        Assert.Equal(first.ObservedAtUtc, cached.ObservedAtUtc);
        Assert.Equal(clock.GetUtcNow().AddMinutes(1), cached.CacheExpiresAtUtc);

        clock.Advance(TimeSpan.FromMinutes(1));
        var afterExpiry = await cache.GetOrRefreshAsync(
            "ARM-01", TimeSpan.FromMinutes(1), false, Scan, CancellationToken.None);

        Assert.False(afterExpiry.IsCached);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_scan_and_fresh_bypasses_cache()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-04T01:00:00Z"));
        var cache = new AuboArmProgramCatalogCache(clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AuboArmProgramCatalogResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<AuboArmProgramCatalogResponse> Scan(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            started.SetResult();
            return await release.Task;
        }

        var firstTask = cache.GetOrRefreshAsync(
            "ARM-01", TimeSpan.FromMinutes(1), false, Scan, CancellationToken.None);
        await started.Task;
        var secondTask = cache.GetOrRefreshAsync(
            "ARM-01", TimeSpan.FromMinutes(1), false, Scan, CancellationToken.None);
        release.SetResult(CompleteCatalog(clock.GetUtcNow()));

        var results = await Task.WhenAll(firstTask, secondTask);
        Assert.Equal(1, calls);
        Assert.All(results, result => Assert.False(result.IsCached));

        var fresh = await cache.GetOrRefreshAsync(
            "ARM-01", TimeSpan.FromMinutes(1), true, _ =>
                Task.FromResult(CompleteCatalog(clock.GetUtcNow().AddSeconds(1))),
            CancellationToken.None);

        Assert.False(fresh.IsCached);
        Assert.Equal(clock.GetUtcNow().AddSeconds(1), fresh.ObservedAtUtc);
        var cached = await cache.GetOrRefreshAsync(
            "ARM-01", TimeSpan.FromMinutes(1), false,
            _ => throw new InvalidOperationException("cache should have been populated"),
            CancellationToken.None);
        Assert.True(cached.IsCached);
    }

    [Fact]
    public async Task Invalidate_drops_entry_and_old_incomplete_results_are_not_cached()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-04T01:00:00Z"));
        var cache = new AuboArmProgramCatalogCache(clock);
        var calls = 0;
        var incomplete = CompleteCatalog(clock.GetUtcNow()) with
        {
            IsComplete = false,
            ReadErrors = ["slot 2 failed"]
        };

        var first = await cache.GetOrRefreshAsync(
            "ARM-01", TimeSpan.FromMinutes(1), false,
            _ => Task.FromResult(incomplete with { ObservedAtUtc = clock.GetUtcNow() }),
            CancellationToken.None);
        var second = await cache.GetOrRefreshAsync(
            "ARM-01", TimeSpan.FromMinutes(1), false,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(CompleteCatalog(clock.GetUtcNow()));
            },
            CancellationToken.None);

        Assert.False(first.IsCached);
        Assert.False(second.IsCached);
        Assert.Equal(1, calls);

        cache.Invalidate("ARM-01");
        var afterInvalidate = await cache.GetOrRefreshAsync(
            "ARM-01", TimeSpan.FromMinutes(1), false,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(CompleteCatalog(clock.GetUtcNow().AddSeconds(1)));
            },
            CancellationToken.None);

        Assert.False(afterInvalidate.IsCached);
        Assert.Equal(2, calls);
    }

    private static AuboArmProgramCatalogResponse CompleteCatalog(DateTimeOffset observedAt) =>
        new(
            "ARM-01",
            true,
            "取料盘",
            ["取料盘"],
            ["取料盘"],
            true,
            [],
            observedAt)
        {
            Slots = [new AuboArmProgramSlot(0, "取料盘")]
        };

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset Current { get; private set; } = initial;

        public override DateTimeOffset GetUtcNow() => Current;

        public void Advance(TimeSpan amount) => Current = Current.Add(amount);
    }
}
