namespace Nexa.Services.Resources;

internal sealed class ResourceSnapshotCache<TKey, TValue>(int capacity = 24) where TKey : notnull
{
    private readonly Dictionary<TKey, (DateTimeOffset At, TValue Value)> _entries = [];
    internal bool TryRead(TKey key, out TValue? value)
    {
        lock (_entries)
        {
            if (_entries.TryGetValue(key, out var entry) && DateTimeOffset.UtcNow - entry.At < TimeSpan.FromMinutes(2)) { value = entry.Value; return true; }
            _entries.Remove(key); value = default; return false;
        }
    }
    internal void Save(TKey key, TValue value)
    {
        lock (_entries)
        {
            if (_entries.Count >= capacity) _entries.Remove(_entries.MinBy(pair => pair.Value.At).Key);
            _entries[key] = (DateTimeOffset.UtcNow, value);
        }
    }
}
