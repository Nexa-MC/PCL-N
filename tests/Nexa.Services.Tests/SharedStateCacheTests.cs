using Nexa.Services.Caching;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private sealed class SharedCacheClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal static async ValueTask SharedCacheCoalescesWaitersWithoutSharingCancellation()
    {
        using SharedStateCache cache = new(); using CancellationTokenSource caller = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0; CancellationToken producerToken = default;
        StateCacheKey key = new("test.query", "same", "revision-1");
        StateCachePolicy policy = new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), 32);
        async ValueTask<string> Produce(CancellationToken token)
        { Interlocked.Increment(ref calls); producerToken = token; await release.Task.ConfigureAwait(false); return "complete"; }
        Task<string> first = cache.GetOrCreateAsync(key, policy, Produce, cancellationToken: caller.Token).AsTask();
        Task<string> second = cache.GetOrCreateAsync(key, policy, Produce).AsTask();
        caller.Cancel(); bool cancelled = false;
        try { await first.ConfigureAwait(false); } catch (OperationCanceledException) { cancelled = true; }
        AssertTrue(cancelled); AssertFalse(producerToken.IsCancellationRequested); AssertEqual(1, calls);
        release.SetResult(); AssertEqual("complete", await second.ConfigureAwait(false));
        AssertTrue(cache.TryGet<string>(key, out var cached)); AssertEqual("complete", cached!.Value);
    }

    internal static async ValueTask SharedCacheInvalidationBlocksOlderPublication()
    {
        using SharedStateCache cache = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StateCacheKey key = new("test.query", "same", "revision-1");
        StateCachePolicy policy = new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), 32);
        Task<string> older = cache.GetOrCreateAsync(key, policy, async _ =>
        { await release.Task.ConfigureAwait(false); return "older"; }).AsTask();
        cache.InvalidateScope("test.query");
        AssertEqual("newer", await cache.GetOrCreateAsync(key, policy, _ => ValueTask.FromResult("newer")));
        release.SetResult(); AssertEqual("older", await older.ConfigureAwait(false));
        AssertTrue(cache.TryGet<string>(key, out var cached)); AssertEqual("newer", cached!.Value);
        AssertEqual("partial", await cache.GetOrCreateAsync(key, policy, _ => ValueTask.FromResult("partial"),
            refresh: true, shouldStore: static value => value != "partial"));
        AssertTrue(cache.TryGet<string>(key, out cached)); AssertEqual("newer", cached!.Value);
    }

    internal static void SharedCacheEnforcesLruBytesAndOriginalAge()
    {
        SharedCacheClock clock = new(); using SharedStateCache cache = new(2, 16, timeProvider: clock);
        StateCacheKey a = new("test.query", "a", "1"), b = new("test.query", "b", "1"), c = new("test.query", "c", "1");
        StateCachePolicy policy = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), 8);
        cache.Store(a, "a", policy); cache.Store(b, "b", policy); AssertTrue(cache.TryGet<string>(a, out _)); cache.Store(c, "c", policy);
        AssertFalse(cache.TryGet<string>(b, out _)); AssertTrue(cache.TryGet<string>(a, out _)); AssertTrue(cache.TryGet<string>(c, out _));
        clock.Now += TimeSpan.FromSeconds(6);
        AssertFalse(cache.TryGet<string>(a, out _)); AssertTrue(cache.TryGet<string>(a, out var stale, allowStale: true)); AssertTrue(stale!.IsStale);
        cache.Store(b, "imported", policy, clock.Now - TimeSpan.FromSeconds(9));
        AssertTrue(cache.TryGet<string>(b, out var imported, allowStale: true)); AssertTrue(imported!.IsStale);
        clock.Now += TimeSpan.FromSeconds(2); AssertFalse(cache.TryGet<string>(b, out _, allowStale: true));
        cache.Store(a, "too-large", policy with { SizeBytes = 17 }); AssertFalse(cache.TryGet<string>(a, out _, allowStale: true));
    }

    internal static async ValueTask SharedCacheStoreDuringFlightKeepsProducerSharedAndRefreshPublication()
    {
        SharedCacheClock clock = new(); using SharedStateCache cache = new(timeProvider: clock);
        StateCacheKey key = new("test.query", "same", "revision-1");
        StateCachePolicy policy = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), 8);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int producers = 0;
        async ValueTask<string> Produce(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref producers); await release.Task.ConfigureAwait(false); return "live"; }
        Task<string> first = cache.GetOrCreateAsync(key, policy, Produce).AsTask();
        cache.Store(key, "offline", policy, clock.Now - TimeSpan.FromSeconds(10));
        AssertTrue(cache.TryGet<string>(key, out var seeded, allowStale: true)); AssertEqual("offline", seeded!.Value);
        Task<string> second = cache.GetOrCreateAsync(key, policy, Produce, refresh: true).AsTask();
        AssertEqual(1, producers); release.SetResult();
        AssertEqual("live", await first); AssertEqual("live", await second);
        cache.Store(key, "older-disk-record", policy, clock.Now - TimeSpan.FromSeconds(1));
        AssertTrue(cache.TryGet<string>(key, out var final)); AssertEqual("live", final!.Value); AssertFalse(final.IsStale);
    }
}
