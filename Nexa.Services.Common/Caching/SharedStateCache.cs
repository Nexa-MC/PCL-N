namespace Nexa.Services.Caching;

/// <summary>One application owner for immutable, non-secret query results; no background timers.</summary>
public sealed class SharedStateCache : ISharedStateCache, IDisposable
{
    private sealed record Entry(Type Type, object Value, DateTimeOffset StoredAt, StateCachePolicy Policy,
        LinkedListNode<StateCacheKey> Recency);
    private sealed class Flight(Type type)
    {
        public Type Type { get; } = type;
        public TaskCompletionSource<object> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private readonly Dictionary<StateCacheKey, Entry> _entries = [];
    private readonly Dictionary<StateCacheKey, Flight> _flights = [];
    private readonly LinkedList<StateCacheKey> _recency = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _maximumEntries;
    private readonly long _maximumBytes;
    private readonly int _maximumPending;
    private long _bytes;
    private int _pending;
    private bool _disposed;

    public SharedStateCache(int maximumEntries = 128, long maximumBytes = 32L * 1024 * 1024,
        int maximumPending = 64, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPending);
        _maximumEntries = maximumEntries; _maximumBytes = maximumBytes; _maximumPending = maximumPending;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryGet<T>(StateCacheKey key,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out StateCacheSnapshot<T>? snapshot, bool allowStale = false)
    {
        ValidateKey(key);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return TryGetLocked(key, out snapshot, allowStale);
        }
    }

    public async ValueTask<T> GetOrCreateAsync<T>(StateCacheKey key, StateCachePolicy policy,
        Func<CancellationToken, ValueTask<T>> factory, bool refresh = false,
        Func<T, bool>? shouldStore = null, CancellationToken cancellationToken = default)
    {
        ValidateKey(key); ValidatePolicy(policy); ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();
        Flight flight; bool produce = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!refresh && TryGetLocked<T>(key, out var snapshot, false)) return snapshot!.Value;
            if (_entries.TryGetValue(key, out Entry? entry)) CheckType<T>(entry.Type);
            if (_flights.TryGetValue(key, out Flight? existing))
            { CheckType<T>(existing.Type); flight = existing; }
            else
            {
                if (_pending >= _maximumPending) throw new InvalidOperationException("Shared query work budget is exhausted.");
                flight = new(typeof(T)); _flights.Add(key, flight); ++_pending; produce = true;
            }
        }
        if (produce) _ = ProduceAsync(key, policy, factory, shouldStore, flight);
        // A caller only owns its wait. The shared producer uses the application lifetime token.
        return (T)await flight.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Store<T>(StateCacheKey key, T value, StateCachePolicy policy, DateTimeOffset? storedAt = null)
    {
        ValidateKey(key); ValidatePolicy(policy); ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DateTimeOffset at = storedAt ?? _timeProvider.GetUtcNow();
            if (at > _timeProvider.GetUtcNow()) throw new ArgumentOutOfRangeException(nameof(storedAt));
            if (_flights.TryGetValue(key, out Flight? flight)) CheckType<T>(flight.Type);
            if (_entries.TryGetValue(key, out Entry? current))
            {
                CheckType<T>(current.Type);
                if (storedAt.HasValue && current.StoredAt >= at) return;
            }
            SetLocked(key, value, policy, at);
        }
    }

    public void Invalidate(StateCacheKey key)
    {
        ValidateKey(key);
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); RemoveLocked(key); _flights.Remove(key); }
    }

    public void InvalidateScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (StateCacheKey key in _entries.Keys.Where(key => key.Scope == scope).ToArray()) RemoveLocked(key);
            foreach (StateCacheKey key in _flights.Keys.Where(key => key.Scope == scope).ToArray()) _flights.Remove(key);
        }
    }

    private async Task ProduceAsync<T>(StateCacheKey key, StateCachePolicy policy,
        Func<CancellationToken, ValueTask<T>> factory, Func<T, bool>? shouldStore, Flight flight)
    {
        try
        {
            T value = await factory(_lifetime.Token).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(value);
            bool admit = shouldStore?.Invoke(value) ?? true;
            lock (_gate)
            {
                if (!_disposed && _flights.TryGetValue(key, out Flight? current) && ReferenceEquals(current, flight) && admit)
                    SetLocked(key, value, policy, _timeProvider.GetUtcNow());
                FinishFlightLocked(key, flight);
            }
            flight.Completion.TrySetResult(value);
        }
        catch (OperationCanceledException error)
        {
            lock (_gate) FinishFlightLocked(key, flight);
            flight.Completion.TrySetCanceled(error.CancellationToken);
        }
        catch (Exception error)
        {
            lock (_gate) FinishFlightLocked(key, flight);
            flight.Completion.TrySetException(error);
        }
    }

    private void FinishFlightLocked(StateCacheKey key, Flight flight)
    {
        --_pending;
        if (_flights.TryGetValue(key, out Flight? current) && ReferenceEquals(current, flight)) _flights.Remove(key);
    }

    private bool TryGetLocked<T>(StateCacheKey key, out StateCacheSnapshot<T>? snapshot, bool allowStale)
    {
        snapshot = null;
        if (!_entries.TryGetValue(key, out Entry? entry)) return false;
        CheckType<T>(entry.Type);
        TimeSpan age = _timeProvider.GetUtcNow() - entry.StoredAt;
        if (age >= entry.Policy.RetainFor) { RemoveLocked(key); return false; }
        bool stale = age >= entry.Policy.FreshFor;
        if (stale && !allowStale) return false;
        _recency.Remove(entry.Recency); _recency.AddLast(entry.Recency);
        snapshot = new((T)entry.Value, entry.StoredAt, stale); return true;
    }

    private void SetLocked<T>(StateCacheKey key, T value, StateCachePolicy policy, DateTimeOffset at)
    {
        if (_entries.TryGetValue(key, out Entry? current)) CheckType<T>(current.Type);
        RemoveLocked(key);
        if (policy.SizeBytes > _maximumBytes || _timeProvider.GetUtcNow() - at >= policy.RetainFor) return;
        while (_entries.Count >= _maximumEntries || _bytes > _maximumBytes - policy.SizeBytes)
            RemoveLocked(_recency.First!.Value);
        LinkedListNode<StateCacheKey> recency = _recency.AddLast(key);
        _entries.Add(key, new(typeof(T), value!, at, policy, recency)); _bytes += policy.SizeBytes;
    }

    private void RemoveLocked(StateCacheKey key)
    {
        if (!_entries.Remove(key, out Entry? entry)) return;
        _bytes -= entry.Policy.SizeBytes; _recency.Remove(entry.Recency);
    }

    private static void CheckType<T>(Type stored)
    { if (stored != typeof(T)) throw new InvalidOperationException("A shared cache key was used with incompatible result types."); }

    private static void ValidateKey(StateCacheKey key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Scope); ArgumentException.ThrowIfNullOrWhiteSpace(key.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Revision);
        if (key.Scope.Length > 256 || key.Key.Length > 16384 || key.Revision.Length > 16384)
            throw new ArgumentOutOfRangeException(nameof(key));
    }

    private static void ValidatePolicy(StateCachePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.FreshFor < TimeSpan.Zero || policy.RetainFor <= TimeSpan.Zero || policy.FreshFor > policy.RetainFor || policy.SizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(policy));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _entries.Clear(); _recency.Clear(); _flights.Clear(); _bytes = 0;
        }
        _lifetime.Cancel();
        // Producers may still inspect the token while cancellation propagates.
    }
}
