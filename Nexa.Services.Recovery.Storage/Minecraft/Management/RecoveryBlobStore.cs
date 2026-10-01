using System.IO.Compression;
using System.Security.Cryptography;
using Nexa.Services.Scheduling;

namespace Nexa.Services.Minecraft.Management;

internal sealed record RecoveryBlob(string Sha256, long Length);

/// <summary>One capture/restore transaction's actual uncompressed byte budget.</summary>
internal sealed class RecoveryByteBudget(long limit)
{
    private long _remaining = limit >= 0 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));
    internal int ReadSize(int maximum) => (int)Math.Min(maximum - 1L, Math.Max(0, Interlocked.Read(ref _remaining))) + 1;
    internal void Consume(int count)
    {
        if (Interlocked.Add(ref _remaining, -count) < 0)
            throw new InvalidDataException("快照内容超过本次操作的大小限制。");
    }
}

/// <summary>
/// Immutable, locally compressed content objects. This is not a snapshot manifest: callers must
/// commit a complete manifest separately and restore only through staging files, never live files.
/// </summary>
internal sealed class RecoveryBlobStore
{
    internal const long MaxFileBytes = 512L * 1024 * 1024;
    internal const long MaxTransactionBytes = 8L * 1024 * 1024 * 1024;
    private readonly string _root;
    private readonly string _objects;

    internal RecoveryBlobStore(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("快照目录必须为绝对路径。", nameof(directory));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _objects = Path.Combine(_root, "objects");
    }

    internal Task<RecoveryBlob> StoreAsync(Stream source, long expectedLength, RecoveryByteBudget budget,
        CancellationToken token = default) => StoreAsync(source, expectedLength, budget, null, token);

    internal async Task<RecoveryBlob> StoreAsync(Stream source, long expectedLength, RecoveryByteBudget budget,
        IWorkScheduler? work, CancellationToken token = default)
    {
        ValidateLength(expectedLength);
        string temporary = Path.Combine(_objects, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            string hash;
            FileStream output;
            using (IDisposable? admission = await AdmitSourceAsync(work, token).ConfigureAwait(false))
            {
                EnsureDirectory(_objects);
                output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            }
            await using (output)
            {
                await using (var compressed = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
                    hash = await CopyAndHashAsync(source, compressed, expectedLength, budget, token, work).ConfigureAwait(false);
                using IDisposable? admission = await AdmitSourceAsync(work, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            var blob = new RecoveryBlob(hash, expectedLength);
            // Multiple producers may stage concurrently. Publication and reuse verification are
            // protected across processes; the permanent lock file must never be deleted on release.
            using IDisposable? publication = await AdmitSourceAsync(work, token).ConfigureAwait(false);
            await using var lease = await AcquireAsync(".objects.lock", token).ConfigureAwait(false);
            string destination = BlobPath(blob);
            CheckLinks(destination);
            // The staged object was just hashed from the source. Replacing an existing object
            // repairs corrupt dedupe hits without decompressing and hashing them a second time.
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return blob;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal bool TryGetStamp(RecoveryBlob blob, out long size, out long modified)
    {
        string path = BlobPath(blob);
        CheckLinks(path);
        var info = new FileInfo(path);
        size = info.Exists ? info.Length : -1;
        modified = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
        return info.Exists && size <= blob.Length + 65536;
    }

    internal async Task CopyVerifiedAsync(RecoveryBlob blob, Stream stagingDestination, RecoveryByteBudget budget, CancellationToken token = default)
    {
        ValidateLength(blob.Length);
        string path = BlobPath(blob);
        CheckLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > blob.Length + 65536)
            throw new InvalidDataException("快照对象缺失或大小异常。");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var compressed = new BrotliStream(input, CompressionMode.Decompress);
        string actual = await CopyAndHashAsync(compressed, stagingDestination, blob.Length, budget, token).ConfigureAwait(false);
        if (!string.Equals(actual, blob.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("快照对象校验失败，未允许提交恢复文件。");
    }

    private static async ValueTask<IDisposable?> AdmitSourceAsync(IWorkScheduler? work, CancellationToken token) =>
        work is null ? null : await work.AcquireAsync(WorkPriority.Background,
            WorkResource.Cpu | WorkResource.Disk, token).ConfigureAwait(false);

    internal static async Task<string> CopyAndHashAsync(Stream source, Stream destination, long expectedLength,
        RecoveryByteBudget budget, CancellationToken token, IWorkScheduler? work = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long total = 0;
        try
        {
            while (true)
            {
                using IDisposable? admission = await AdmitSourceAsync(work, token).ConfigureAwait(false);
                // At the boundary read only one extra byte to distinguish EOF from a false size.
                int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(budget.ReadSize(buffer.Length), expectedLength - total + 1)), token).ConfigureAwait(false);
                if (read == 0) break;
                if (read > expectedLength - total) throw new InvalidDataException("快照文件的实际长度与声明不一致。");
                budget.Consume(read);
                hash.AppendData(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                total += read;
            }
            if (total != expectedLength) throw new InvalidDataException("快照文件读取不完整。");
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private string BlobPath(RecoveryBlob blob)
    {
        if (blob.Sha256 is not { Length: 64 } || blob.Sha256.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new InvalidDataException("快照对象标识无效。");
        return Path.Combine(_objects, blob.Sha256 + ".br");
    }

    internal async Task<FileStream> AcquireManifestLeaseAsync(CancellationToken token)
    {
        EnsureDirectory(_root);
        return await AcquireAsync(".manifest.lock", token).ConfigureAwait(false);
    }

    // Caller must hold the manifest lease. Pending recovery journals pin their objects.
    internal async Task CollectUnreferencedAsync(IReadOnlySet<string> retained, CancellationToken token,
        IWorkScheduler? work = null)
    {
        IDisposable? TryAdmission()
        {
            token.ThrowIfCancellationRequested();
            try { return work?.TryAcquire(WorkPriority.Idle, WorkResource.Cpu | WorkResource.Disk, token); }
            catch (ObjectDisposedException) { return null; }
        }

        FileStream objectLease;
        using (IDisposable? probe = TryAdmission())
        {
            if (work is not null && probe is null) return;
            string transactions = Path.Combine(_root, "transactions");
            CheckLinks(transactions);
            if (Directory.Exists(transactions) || !Directory.Exists(_objects)) return;
            objectLease = await AcquireAsync(".objects.lock", token).ConfigureAwait(false);
        }
        await using (objectLease)
        {
            IEnumerator<string>? paths = null;
            try
            {
                int visited = 0;
                while (visited < 100000)
                {
                    // Never queue shared admission while holding the object lock. Even directory
                    // iteration runs inside a bounded lease; quiet/contention defers the remainder.
                    using IDisposable? chunk = TryAdmission();
                    if (work is not null && chunk is null) return;
                    CheckLinks(_objects);
                    paths ??= Directory.EnumerateFiles(_objects, "*.br").GetEnumerator();
                    for (int count = 0; count < 32 && visited < 100000; count++, visited++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!paths.MoveNext()) return;
                        string path = paths.Current;
                        string hash = Path.GetFileNameWithoutExtension(path);
                        if (hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')) || retained.Contains(hash)) continue;
                        CheckLinks(path);
                        File.Delete(path);
                    }
                }
            }
            finally { paths?.Dispose(); }
        }
    }

    private async Task<FileStream> AcquireAsync(string name, CancellationToken token)
    {
        string path = Path.Combine(_root, name);
        long started = Environment.TickCount64;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            CheckLinks(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 - started < 5000)
            { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }

    private static void ValidateLength(long length)
    {
        if (length is < 0 or > MaxFileBytes) throw new InvalidDataException("快照中的单个文件超过大小限制。");
    }

    private static void EnsureDirectory(string path)
    {
        CheckLinks(path); Directory.CreateDirectory(path); CheckLinks(path);
    }

    internal static void CheckLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("快照路径不能包含链接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
