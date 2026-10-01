namespace Nexa.Services.Minecraft.Downloads;

/// <summary>Successful hash receipts belong to one service lifetime; explicit verify bypasses them.</summary>
internal sealed class MinecraftFileVerificationCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Receipt> _receipts = new(Nexa.Core.PathIdentity.Comparer);
    private sealed record Receipt(long Length, long Modified, string? Hash);

    internal async ValueTask<bool> VerifyAsync(MinecraftExpectedFile file, CancellationToken token,
        bool contentAddressed = false, bool forceHash = false)
    {
        token.ThrowIfCancellationRequested();
        string path = Path.GetFullPath(file.Path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || file.Size is { } size && info.Length != size) return false;
        var stamp = new Receipt(info.Length, info.LastWriteTimeUtc.Ticks, file.Sha1?.ToUpperInvariant());
        if (!forceHash)
        {
            if (contentAddressed && file.Size is > 0 && file.Sha1 is { Length: 40 } hash
                && Path.GetFileName(path).Equals(hash, StringComparison.OrdinalIgnoreCase)) return true;
            lock (_gate) if (_receipts.TryGetValue(path, out var receipt) && receipt == stamp) return true;
        }
        if (!await MinecraftFileVerifier.VerifyAsync(file, token).ConfigureAwait(false)) return false;
        info.Refresh();
        if (!info.Exists || info.Length != stamp.Length || info.LastWriteTimeUtc.Ticks != stamp.Modified) return false;
        lock (_gate)
        {
            if (_receipts.Count >= 8192) _receipts.Clear();
            _receipts[path] = stamp;
        }
        return true;
    }
}
