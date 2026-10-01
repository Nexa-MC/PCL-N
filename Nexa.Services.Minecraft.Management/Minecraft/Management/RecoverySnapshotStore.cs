using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Services.Scheduling;

namespace Nexa.Services.Minecraft.Management;

internal sealed record RecoverySource(string Area, string RelativePath);
internal sealed record RecoverySnapshotFile(RecoverySource Source, RecoveryBlob Blob,
    long? ModifiedTicks = null, long? BlobSize = null, long? BlobModifiedTicks = null);
internal sealed record RecoverySnapshot(Guid Revision, string InstanceDirectory, string GameDirectory,
    DateTimeOffset CapturedAt, IReadOnlyList<RecoverySnapshotFile> Files, string SettingsDocument);

/// <summary>
/// Atomically publishes a fully copied capture plan. Policy owns plan enumeration, launch success
/// admission, settings extraction and recovery transactions; this store never edits live game files.
/// </summary>
internal sealed partial class RecoverySnapshotStore
{
    internal const int MaxFiles = 10000;
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private readonly string _instance, _game, _directory, _manifest;
    private readonly RecoveryBlobStore _blobs;

    private readonly IWorkScheduler? _work;

    internal RecoverySnapshotStore(string instanceDirectory, string gameDirectory, IWorkScheduler? work = null)
    {
        _instance = Normalize(instanceDirectory); _game = Normalize(gameDirectory);
        _directory = Path.Combine(_instance, "Nexa", "Recovery");
        _manifest = Path.Combine(_directory, "baseline.json");
        _blobs = new(_directory);
        _work = work;
    }

    internal Task<RecoverySnapshot> CaptureAsync(IReadOnlyList<RecoverySource> sources, string settingsDocument, CancellationToken token = default) =>
        CaptureAsync(sources, settingsDocument, null, token);

    internal Task<RecoverySnapshot> CaptureAsync(IReadOnlyList<RecoverySource> sources, string settingsDocument,
        Func<CancellationToken, Task>? validatePlan, CancellationToken token = default) =>
        CaptureAsync(sources, settingsDocument, validatePlan, false, token);

    internal async Task<RecoverySnapshot> CaptureAsync(IReadOnlyList<RecoverySource> sources, string settingsDocument,
        Func<CancellationToken, Task>? validatePlan, bool retainHistory, CancellationToken token = default)
    {
        if (sources.Count > MaxFiles) throw new InvalidDataException("快照文件数量超过限制。");
        JsonObject settings = ParseSettings(settingsDocument);
        var paths = sources.Select(ResolveSource).ToArray();
        if (paths.Distinct(Nexa.Core.PathIdentity.Comparer).Count() != paths.Length)
            throw new InvalidDataException("快照计划包含重复文件。");
        await using var lease = await _blobs.AcquireManifestLeaseAsync(token).ConfigureAwait(false);
        RecoverySnapshot? previous;
        try { previous = await ReadUnderManifestLeaseAsync(token).ConfigureAwait(false); }
        catch (InvalidDataException) { previous = null; }
        var receipts = previous?.Files.ToDictionary(file => ResolveSource(file.Source), Nexa.Core.PathIdentity.Comparer)
            ?? new Dictionary<string, RecoverySnapshotFile>(Nexa.Core.PathIdentity.Comparer);
        var captured = new RecoverySnapshotFile[paths.Length];
        var observed = new (string Path, long Length, long Modified)[paths.Length];
        var budget = new RecoveryByteBudget(RecoveryBlobStore.MaxTransactionBytes);
        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Length), new ParallelOptions
        { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (index, token) =>
        {
            string path = paths[index];
            FileStream? source = null;
            try
            {
                long length, modified;
                RecoveryBlob? blob = null;
                using (IDisposable? admission = _work is null ? null : await _work.AcquireAsync(
                    WorkPriority.Background, WorkResource.Cpu | WorkResource.Disk, token).ConfigureAwait(false))
                {
                    RecoveryBlobStore.CheckLinks(path);
                    var info = new FileInfo(path);
                    length = info.Length; modified = info.LastWriteTimeUtc.Ticks;
                    if (receipts.TryGetValue(path, out var receipt) && receipt.ModifiedTicks == modified
                        && receipt.Blob.Length == length && _blobs.TryGetStamp(receipt.Blob, out var size, out var blobModified)
                        && receipt.BlobSize == size && receipt.BlobModifiedTicks == blobModified)
                    {
                        budget.Consume(checked((int)length));
                        blob = receipt.Blob;
                    }
                    else source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                }
                if (source is not null)
                    blob = await _blobs.StoreAsync(source, length, budget, _work, token).ConfigureAwait(false);
                if (blob is null) throw new IOException("快照文件未生成对象。");
                using (IDisposable? admission = _work is null ? null : await _work.AcquireAsync(
                    WorkPriority.Background, WorkResource.Cpu | WorkResource.Disk, token).ConfigureAwait(false))
                {
                    if (!_blobs.TryGetStamp(blob, out var storedSize, out var storedModified))
                        throw new IOException("快照对象发布后缺失。");
                    captured[index] = new(sources[index], blob, modified, storedSize, storedModified);
                    observed[index] = (path, length, modified);
                }
            }
            finally { if (source is not null) await source.DisposeAsync().ConfigureAwait(false); }
        }).ConfigureAwait(false);
        // Equal source contents may publish the same object concurrently. Record its final
        // stamp after every producer has joined, so all references share one reuse receipt.
        for (int index = 0; index < captured.Length; index++)
        {
            if (!_blobs.TryGetStamp(captured[index].Blob, out var size, out var modified))
                throw new IOException("快照对象发布后缺失。");
            captured[index] = captured[index] with { BlobSize = size, BlobModifiedTicks = modified };
        }
        var files = Array.AsReadOnly(captured);
        if (validatePlan is not null) await validatePlan(token).ConfigureAwait(false);
        foreach (var item in observed)
        {
            token.ThrowIfCancellationRequested();
            RecoveryBlobStore.CheckLinks(item.Path);
            var info = new FileInfo(item.Path);
            if (!info.Exists || info.Length != item.Length || info.LastWriteTimeUtc.Ticks != item.Modified)
                throw new IOException("采集期间文件发生变化，已保留上一个快照。");
        }
        var snapshot = new RecoverySnapshot(Guid.NewGuid(), _instance, _game, DateTimeOffset.UtcNow, files, settings.ToJsonString());
        var manifest = EncodeManifest(snapshot);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, RecoveryJsonContext.Default.JsonObject);
        if (bytes.Length > MaxManifestBytes) throw new InvalidDataException("快照清单超过大小限制。");
        string temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".manifest.part");
        try
        {
            RecoveryBlobStore.CheckLinks(_manifest);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            var history = await ReadHistoryUnderLeaseAsync(token).ConfigureAwait(false);
            if (retainHistory) await ArchiveCurrentUnderLeaseAsync(history, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, _manifest, overwrite: true);
            // Cleanup failure cannot undo a committed baseline. Retry on the next capture.
            try
            {
                if (!retainHistory) DeleteHistoryUnderLease(history);
                var retained = files.Select(file => file.Blob.Sha256).ToHashSet(StringComparer.Ordinal);
                if (retainHistory)
                    foreach (var old in history) retained.UnionWith(old.Files.Select(file => file.Blob.Sha256));
                await _blobs.CollectUnreferencedAsync(retained, token, _work).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException) { }
            return snapshot;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal async Task<RecoverySnapshot?> ReadAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        RecoveryBlobStore.CheckLinks(_manifest);
        if (!File.Exists(_manifest)) return null;
        await using var lease = await _blobs.AcquireManifestLeaseAsync(token).ConfigureAwait(false);
        return await ReadUnderManifestLeaseAsync(token).ConfigureAwait(false);
    }

    // Caller owns the manifest lease through preparation, preventing capture/GC races.
    internal async Task<RecoverySnapshot?> ReadUnderManifestLeaseAsync(CancellationToken token)
    {
        return await ReadManifestFileAsync(_manifest, token).ConfigureAwait(false);
    }

    private async Task<RecoverySnapshot?> ReadManifestFileAsync(string path, CancellationToken token, bool allowPreviousGame = false)
    {
        token.ThrowIfCancellationRequested();
        RecoveryBlobStore.CheckLinks(path);
        if (!File.Exists(path)) return null;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        long length = input.Length;
        if (length > MaxManifestBytes) throw new InvalidDataException("快照清单超过大小限制。");
        byte[] bytes = new byte[(int)length + 1];
        int total = 0, read;
        while (total < bytes.Length && (read = await input.ReadAsync(bytes.AsMemory(total), token).ConfigureAwait(false)) != 0) total += read;
        if (total != length) throw new InvalidDataException("快照清单读取期间发生变化。");
        var document = JsonNode.Parse(bytes.AsSpan(0, total)) as JsonObject ?? throw new InvalidDataException("快照清单无效。");
        if (allowPreviousGame && document["game"]?.GetValue<string>() is { } previousGame && previousGame != _game)
        {
            string? root = Directory.GetParent(_instance)?.Parent?.FullName;
            if (!Nexa.Core.PathIdentity.Comparer.Equals(previousGame, _instance)
                && !Nexa.Core.PathIdentity.Comparer.Equals(previousGame, root)) throw new InvalidDataException("历史快照游戏目录无效。");
            return new RecoverySnapshotStore(_instance, previousGame).DecodeManifest(document);
        }
        return DecodeManifest(document);
    }

    internal RecoverySnapshot DecodeManifest(JsonObject document)
    {
        if (document["version"]?.GetValue<int>() != 1
            || !Nexa.Core.PathIdentity.Comparer.Equals(document["instance"]?.GetValue<string>(), _instance)
            || !Nexa.Core.PathIdentity.Comparer.Equals(document["game"]?.GetValue<string>(), _game)
            || document["files"] is not JsonArray entries || entries.Count > MaxFiles)
            throw new InvalidDataException("快照清单不属于当前实例或格式不受支持。");
        List<RecoverySnapshotFile> files = [];
        HashSet<string> unique = new(Nexa.Core.PathIdentity.Comparer);
        long totalLength = 0;
        foreach (var item in entries)
        {
            var source = new RecoverySource(item?["area"]?.GetValue<string>() ?? "", item?["path"]?.GetValue<string>() ?? "");
            if (!unique.Add(ResolveSource(source))) throw new InvalidDataException("快照清单包含重复路径。");
            string hash = item?["sha256"]?.GetValue<string>() ?? "";
            long size = item?["length"]?.GetValue<long>() ?? -1;
            if (size is < 0 or > RecoveryBlobStore.MaxFileBytes || hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
                throw new InvalidDataException("快照对象记录无效。");
            totalLength += size;
            if (totalLength > RecoveryBlobStore.MaxTransactionBytes) throw new InvalidDataException("快照总大小超过限制。");
            long? modified = item?["modifiedTicks"]?.GetValue<long>();
            long? blobSize = item?["blobSize"]?.GetValue<long>();
            long? blobModified = item?["blobModifiedTicks"]?.GetValue<long>();
            if (modified is < 0 || blobModified is < 0 || blobSize is < 0 || blobSize > size + 65536)
                throw new InvalidDataException("快照复用记录无效。");
            files.Add(new(source, new(hash, size), modified, blobSize, blobModified));
        }
        if (!Guid.TryParse(document["revision"]?.GetValue<string>(), out var revision)
            || !DateTimeOffset.TryParseExact(document["capturedAt"]?.GetValue<string>(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var capturedAt))
            throw new InvalidDataException("快照版本或时间无效。");
        return new(revision, _instance, _game, capturedAt,
            files.AsReadOnly(), ParseSettings(document["settings"]?.ToJsonString() ?? "").ToJsonString());
    }

    internal string ResolveSource(RecoverySource source)
    {
        string root = source.Area switch
        {
            "instance" => _instance,
            "game" => _game,
            "root" when Directory.GetParent(_instance) is { Name: "versions", Parent: { } parent } => parent.FullName,
            _ => throw new InvalidDataException("快照文件区域无效。")
        };
        string relative = source.RelativePath.Replace('\\', '/');
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':') || part.TrimEnd(' ', '.') != part))
            throw new InvalidDataException("快照相对路径无效。");
        if (source.Area == "root" && (relative.Split('/') is not ["versions", _, var file]
            || !new[] { ".json", ".jar" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("快照依赖只能包含版本清单与核心文件。");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string comparisonPath = Path.GetRelativePath(_directory, full);
        if (comparisonPath == "." || !comparisonPath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(comparisonPath))
            throw new InvalidDataException("不能将快照目录本身作为备份来源。");
        return full;
    }

    private static JsonObject ParseSettings(string document)
    {
        if (document.Length > 1024 * 1024) throw new InvalidDataException("启动设置快照超过大小限制。");
        return JsonNode.Parse(document) as JsonObject ?? throw new InvalidDataException("启动设置快照无效。");
    }

    private static string Normalize(string path) => Path.IsPathFullyQualified(path)
        ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : throw new InvalidDataException("实例目录必须为绝对路径。");
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal partial class RecoveryJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
