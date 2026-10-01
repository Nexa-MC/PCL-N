namespace Nexa.Services.Minecraft.Downloads;

/// <summary>Successful hash receipts belong to one service lifetime; explicit verify bypasses them.</summary>
internal sealed class MinecraftFileVerificationCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<CachedReceipt>> _receipts = new(Nexa.Core.PathIdentity.Comparer);
    private readonly LinkedList<CachedReceipt> _recent = new();
    private readonly int _capacity;
    private sealed record Receipt(long Length, long Modified, string? Hash);
    private sealed record CachedReceipt(string Path, Receipt Stamp);

    internal MinecraftFileVerificationCache(int capacity = 8192)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    internal async ValueTask<bool> VerifyAsync(MinecraftExpectedFile file, CancellationToken token,
        bool contentAddressed = false, bool forceHash = false)
    {
        token.ThrowIfCancellationRequested();
        string path = Path.GetFullPath(file.Path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || file.Size is { } size && info.Length != size)
        {
            lock (_gate) RemoveLocked(path);
            return false;
        }
        var stamp = new Receipt(info.Length, info.LastWriteTimeUtc.Ticks, file.Sha1?.ToUpperInvariant());
        if (!forceHash)
        {
            if (contentAddressed && file.Size is > 0 && file.Sha1 is { Length: 40 } hash
                && Path.GetFileName(path).Equals(hash, StringComparison.OrdinalIgnoreCase)) return true;
        }
        lock (_gate)
        {
            if (_receipts.TryGetValue(path, out var node))
            {
                if (!forceHash && node.Value.Stamp == stamp)
                {
                    _recent.Remove(node);
                    _recent.AddFirst(node);
                    return true;
                }
                RemoveLocked(path);
            }
        }
        if (!await MinecraftFileVerifier.VerifyAsync(file, token).ConfigureAwait(false)) return false;
        info.Refresh();
        if (!info.Exists || info.Length != stamp.Length || info.LastWriteTimeUtc.Ticks != stamp.Modified) return false;
        lock (_gate)
        {
            if (_receipts.TryGetValue(path, out var existing))
            {
                existing.Value = new CachedReceipt(path, stamp);
                _recent.Remove(existing);
                _recent.AddFirst(existing);
            }
            else
            {
                if (_receipts.Count >= _capacity) RemoveLocked(_recent.Last!.Value.Path);
                _receipts.Add(path, _recent.AddFirst(new CachedReceipt(path, stamp)));
            }
        }
        return true;
    }

    private void RemoveLocked(string path)
    {
        if (_receipts.Remove(path, out var node)) _recent.Remove(node);
    }
}
