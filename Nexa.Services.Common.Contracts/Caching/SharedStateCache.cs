using System.Diagnostics.CodeAnalysis;

namespace Nexa.Services.Caching;

/// <summary>Request identity qualified by its owner and the revision of its authoritative inputs.</summary>
public readonly record struct StateCacheKey(string Scope, string Key, string Revision);

/// <summary>Explicit freshness, maximum retention and estimated retained payload size.</summary>
public sealed record StateCachePolicy(TimeSpan FreshFor, TimeSpan RetainFor, long SizeBytes = 1);

public sealed record StateCacheSnapshot<T>(T Value, DateTimeOffset StoredAt, bool IsStale);

/// <summary>Application-owned bounded query snapshots. Callers own input revisions and admission.</summary>
public interface ISharedStateCache
{
    bool TryGet<T>(StateCacheKey key, [NotNullWhen(true)] out StateCacheSnapshot<T>? snapshot, bool allowStale = false);
    ValueTask<T> GetOrCreateAsync<T>(StateCacheKey key, StateCachePolicy policy,
        Func<CancellationToken, ValueTask<T>> factory, bool refresh = false,
        Func<T, bool>? shouldStore = null, CancellationToken cancellationToken = default);
    void Store<T>(StateCacheKey key, T value, StateCachePolicy policy, DateTimeOffset? storedAt = null);
    void Invalidate(StateCacheKey key);
    void InvalidateScope(string scope);
}
