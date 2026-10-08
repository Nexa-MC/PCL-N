using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexa.Services.Setup;

/// <summary>Copied immutable CAS objects, pinned by owned manifests. No writable hardlinks.</summary>
public sealed partial class ContentBackupService
{
    public const int MaximumFiles = 8192;
    public const long MaximumFileBytes = 512L * 1024 * 1024;
    public const long MaximumCaptureBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumManifests = 1024;
    private readonly string _root, _objects, _manifests;
    private readonly TimeProvider _clock;
    private readonly Func<bool> _isIdle;

    public ContentBackupService(string directory, Func<bool>? isIdle = null, TimeProvider? clock = null)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("备份目录必须为绝对路径。", nameof(directory));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _objects = Path.Combine(_root, "objects"); _manifests = Path.Combine(_root, "manifests");
        _clock = clock ?? TimeProvider.System; _isIdle = isIdle ?? (() => true);
    }

    public Task<ContentBackupManifest> CaptureAsync(string sourceDirectory, string label, CancellationToken token = default) =>
        CaptureAsync(sourceDirectory, label, new HashSet<string>(StringComparer.Ordinal), token);

    public async Task<ContentBackupManifest> CaptureAsync(string sourceDirectory, string label, IReadOnlySet<string> excludedRelativePaths, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(excludedRelativePaths);
        if (excludedRelativePaths.Count > 64) throw new IOException("备份排除项超过预算。");
        foreach (string path in excludedRelativePaths) ValidateRelative(path);
        var excluded = new HashSet<string>(excludedRelativePaths, Nexa.Core.PathIdentity.Comparer);
        string source = Absolute(sourceDirectory);
        if (Within(source, _root) || Within(_root, source)) throw new IOException("备份来源与对象存储不能相互包含。");
        if (label.Length is < 1 or > 128 || label.Any(char.IsControl)) throw new InvalidDataException("备份名称无效。");
        await using var lease = await AcquireAsync(token).ConfigureAwait(false);
        EnsureIdle();
        if ((await ReadAllAsync(token).ConfigureAwait(false)).Count >= MaximumManifests) throw new IOException("请先清理旧备份。");
        CheckLinks(source);
        string[] CapturePaths() => Enumerate(source, token).Where(path => !excluded.Contains(Relative(source, path))).ToArray();
        var paths = CapturePaths();
        Dictionary<string, (long Bytes, long Modified)> stamps = new(Nexa.Core.PathIdentity.Comparer);
        List<ContentBackupFile> files = []; long total = 0;
        foreach (string path in paths)
        {
            EnsureIdle(); token.ThrowIfCancellationRequested(); CheckLinks(path);
            var sourceInfo = new FileInfo(path);
            long bytes = sourceInfo.Length; stamps.Add(path, (bytes, sourceInfo.LastWriteTimeUtc.Ticks)); total = checked(total + bytes);
            if (bytes > MaximumFileBytes || total > MaximumCaptureBytes) throw new IOException("备份内容超过大小预算。");
            string temporary = Path.Combine(_objects, Guid.NewGuid().ToString("N") + ".part");
            try
            {
                string hash;
                await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    hash = await CopyHashAsync(input, output, bytes, token).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
                }
                string target = ObjectPath(hash); CheckLinks(target);
                // Replacing the immutable object also repairs a corrupt previous deduplication hit.
                EnsureIdle(); token.ThrowIfCancellationRequested(); File.Move(temporary, target, true);
                files.Add(new(Relative(source, path), hash, bytes));
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        // Refuse changes in the source directory while assembling the immutable capture.
        if (!paths.SequenceEqual(CapturePaths(), StringComparer.Ordinal)) throw new IOException("备份来源在捕获期间发生变化。");
        foreach (var (path, stamp) in stamps)
        {
            token.ThrowIfCancellationRequested(); CheckLinks(path); var current = new FileInfo(path);
            if (!current.Exists || current.Length != stamp.Bytes || current.LastWriteTimeUtc.Ticks != stamp.Modified)
                throw new IOException("备份文件在捕获期间发生变化。");
        }
        var manifest = new ContentBackupManifest(Guid.NewGuid().ToString("N"), label, _clock.GetUtcNow(), Array.AsReadOnly(files.ToArray()));
        EnsureIdle(); await WriteManifestAsync(ManifestPath(manifest.Identity), manifest, false, token).ConfigureAwait(false);
        return manifest;
    }

    public async Task<IReadOnlyList<ContentBackupManifest>> ListAsync(CancellationToken token = default)
    {
        await using var lease = await AcquireAsync(token).ConfigureAwait(false);
        return Array.AsReadOnly((await ReadAllAsync(token).ConfigureAwait(false)).OrderByDescending(x => x.CreatedAt).ToArray());
    }

    public async Task<ContentStoreStatistics> StatisticsAsync(CancellationToken token = default)
    {
        await using var lease = await AcquireAsync(token).ConfigureAwait(false);
        var manifests = await ReadAllAsync(token).ConfigureAwait(false);
        var objects = ReadObjects(token); var pinned = References(manifests);
        long physical = objects.Sum(x => x.Value), referenced = objects.Where(x => pinned.Contains(x.Key)).Sum(x => x.Value);
        return new(manifests.Count, objects.Count, physical, manifests.Sum(x => x.Files.Sum(f => f.Bytes)), referenced, physical - referenced);
    }

    public async Task<ContentPrunePreview> PreviewPruneAsync(int keepCount, int keepDays, CancellationToken token = default)
    {
        ValidateRetention(keepCount, keepDays); EnsureConfiguredCount(keepCount);
        await using var lease = await AcquireAsync(token).ConfigureAwait(false);
        return await PrunePreviewUnderLeaseAsync(keepCount, keepDays, token).ConfigureAwait(false);
    }

    public async Task ApplyPruneAsync(int keepCount, int keepDays, string expectedRevision, CancellationToken token = default)
    {
        ValidateRetention(keepCount, keepDays); EnsureConfiguredCount(keepCount);
        await using var lease = await AcquireAsync(token).ConfigureAwait(false); EnsureIdle();
        var preview = await PrunePreviewUnderLeaseAsync(keepCount, keepDays, token).ConfigureAwait(false);
        if (preview.Revision != expectedRevision) throw new IOException("备份清单在预览后发生变化，请重新预览。");
        // Deleting manifests first is crash-safe: interrupted collection leaves unreferenced objects.
        foreach (string identity in preview.BackupIdentities)
        { EnsureIdle(); EnsureConfiguredCount(keepCount); token.ThrowIfCancellationRequested(); string path = ManifestPath(identity); CheckLinks(path); File.Delete(path); }
        var retained = References(await ReadAllAsync(token).ConfigureAwait(false));
        foreach (var entry in ReadObjects(token))
            if (!retained.Contains(entry.Key))
            { EnsureIdle(); EnsureConfiguredCount(keepCount); token.ThrowIfCancellationRequested(); string path = ObjectPath(entry.Key); CheckLinks(path); File.Delete(path); }
    }

    /// <summary>Retention runs only when explicitly enabled by the composition's idle scheduler.</summary>
    public async Task PruneAutomaticallyAsync(int keepCount, int keepDays, CancellationToken token = default)
    {
        if (!_isIdle()) return;
        var preview = await PreviewPruneAsync(keepCount, keepDays, token).ConfigureAwait(false);
        if (_isIdle()) await ApplyPruneAsync(keepCount, keepDays, preview.Revision, token).ConfigureAwait(false);
    }

    public async Task<OfflineReadinessReport> VerifyOfflineAsync(string identity, CancellationToken token = default)
    {
        await using var lease = await AcquireAsync(token).ConfigureAwait(false);
        return await VerifyUnderLeaseAsync(await ReadManifestAsync(ManifestPath(identity), token).ConfigureAwait(false), token).ConfigureAwait(false);
    }

    public async Task ExportThinAsync(string identity, string destination, CancellationToken token = default)
    {
        string target = Absolute(destination);
        if (Within(_root, target)) throw new IOException("导出目标不能位于备份存储内。");
        await using var lease = await AcquireAsync(token).ConfigureAwait(false);
        await WriteManifestAsync(target, await ReadManifestAsync(ManifestPath(identity), token).ConfigureAwait(false), false, token).ConfigureAwait(false);
    }

    public async Task<OfflineReadinessReport> ImportThinAsync(string source, CancellationToken token = default)
    {
        await using var lease = await AcquireAsync(token).ConfigureAwait(false);
        var manifest = await ReadManifestAsync(Absolute(source), token).ConfigureAwait(false);
        var all = await ReadAllAsync(token).ConfigureAwait(false);
        if (all.Count >= MaximumManifests) throw new IOException("备份数量超过预算。");
        if (all.Any(x => x.Identity == manifest.Identity)) throw new IOException("该备份已存在。");
        await WriteManifestAsync(ManifestPath(manifest.Identity), manifest, false, token).ConfigureAwait(false);
        return await VerifyUnderLeaseAsync(manifest, token).ConfigureAwait(false);
    }

    public async Task RestoreAsync(string identity, string destination, CancellationToken token = default)
    {
        string target = Absolute(destination);
        if (Within(_root, target) || Within(target, _root)) throw new IOException("恢复目标与备份存储不能相互包含。");
        CheckLinks(target);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("恢复目标必须为尚未存在的目录。");
        await using var lease = await AcquireAsync(token).ConfigureAwait(false); EnsureIdle();
        var manifest = await ReadManifestAsync(ManifestPath(identity), token).ConfigureAwait(false);
        string stage = target + ".nexa-restore-" + Guid.NewGuid().ToString("N");
        CheckLinks(stage); Directory.CreateDirectory(stage);
        try
        {
            foreach (var file in manifest.Files)
            {
                EnsureIdle(); token.ThrowIfCancellationRequested(); string outputPath = SafePath(stage, file.RelativePath);
                CheckLinks(outputPath); Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using var input = new FileStream(ObjectPath(file.Sha256), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                if (await CopyHashAsync(input, output, file.Bytes, token).ConfigureAwait(false) != file.Sha256) throw new IOException("备份对象校验失败。");
                await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
            }
            EnsureIdle(); token.ThrowIfCancellationRequested(); CheckLinks(target); Directory.Move(stage, target);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private async Task<ContentPrunePreview> PrunePreviewUnderLeaseAsync(int keepCount, int keepDays, CancellationToken token)
    {
        var all = await ReadAllAsync(token).ConfigureAwait(false);
        var ordered = all.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Identity, StringComparer.Ordinal).ToArray();
        // Always retain the newest backup, including a future-dated imported one.
        var remove = ordered.Skip(1).Where((x, index) => index + 1 >= keepCount || x.CreatedAt < _clock.GetUtcNow().AddDays(-keepDays)).ToArray();
        var removeIds = remove.Select(x => x.Identity).ToHashSet(StringComparer.Ordinal);
        var pins = References(all.Where(x => !removeIds.Contains(x.Identity))); var objects = ReadObjects(token);
        var gc = objects.Where(x => !pins.Contains(x.Key)).ToArray();
        string revision = Hash(Encoding.UTF8.GetBytes(string.Join('\n', all.OrderBy(x => x.Identity).Select(x => Encode(x).ToJsonString()))
            + "\n" + string.Join('\n', objects.OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value)) + $"\n{keepCount}:{keepDays}:" + string.Join(',', removeIds.Order())));
        return new(revision, Array.AsReadOnly(remove.Select(x => x.Identity).ToArray()), gc.Length, gc.Sum(x => x.Value));
    }

    private async Task<OfflineReadinessReport> VerifyUnderLeaseAsync(ContentBackupManifest manifest, CancellationToken token)
    {
        List<string> bad = []; int verified = 0;
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string path = ObjectPath(file.Sha256); CheckLinks(path);
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                if (await CopyHashAsync(input, Stream.Null, file.Bytes, token).ConfigureAwait(false) == file.Sha256) verified++; else bad.Add(file.RelativePath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { bad.Add(file.RelativePath); }
        }
        return new(manifest.Identity, manifest.Files.Count, verified, Array.AsReadOnly(bad.ToArray()));
    }

    private async Task<List<ContentBackupManifest>> ReadAllAsync(CancellationToken token)
    {
        CheckLinks(_manifests); List<ContentBackupManifest> result = []; long entries = 0;
        foreach (string path in Directory.EnumerateFiles(_manifests, "*.json"))
        {
            token.ThrowIfCancellationRequested();
            if (result.Count >= MaximumManifests) throw new IOException("备份清单数量超过预算。");
            var item = await ReadManifestAsync(path, token).ConfigureAwait(false);
            if (Path.GetFileNameWithoutExtension(path) != item.Identity) throw new IOException("备份文件身份不一致。");
            entries += item.Files.Count; if (entries > 100000) throw new IOException("备份条目总数超过预算。");
            result.Add(item);
        }
        return result;
    }

    private Dictionary<string, long> ReadObjects(CancellationToken token)
    {
        CheckLinks(_objects); Dictionary<string, long> result = [];
        foreach (string path in Directory.EnumerateFiles(_objects, "*.blob"))
        {
            token.ThrowIfCancellationRequested(); if (result.Count >= 100000) throw new IOException("对象数量超过预算。");
            CheckLinks(path); string identity = Path.GetFileNameWithoutExtension(path); ValidateHash(identity);
            result.Add(identity, new FileInfo(path).Length);
        }
        return result;
    }

    private static async Task<ContentBackupManifest> ReadManifestAsync(string path, CancellationToken token)
    {
        CheckLinks(path); await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (input.Length > 4 * 1024 * 1024) throw new IOException("备份清单超过读取预算。");
        byte[] bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (input.ReadByte() != -1) throw new IOException("备份清单读取期间发生变化。");
        var json = JsonNode.Parse(bytes) as JsonObject ?? throw new IOException("备份清单无效。");
        if (json["schema"]?.GetValue<int>() != 1) throw new IOException("备份清单版本不支持。");
        string identity = json["identity"]!.GetValue<string>(); ValidateIdentity(identity);
        string label = json["label"]!.GetValue<string>(); if (label.Length is < 1 or > 128 || label.Any(char.IsControl)) throw new IOException("备份名称无效。");
        DateTimeOffset created = DateTimeOffset.ParseExact(json["createdAt"]!.GetValue<string>(), "O", CultureInfo.InvariantCulture);
        var values = json["files"] as JsonArray ?? throw new IOException("备份条目缺失。");
        if (values.Count > MaximumFiles) throw new IOException("备份条目超过预算。");
        HashSet<string> paths = new(Nexa.Core.PathIdentity.Comparer); List<ContentBackupFile> files = []; long total = 0;
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested(); string relative = value!["path"]!.GetValue<string>();
            ValidateRelative(relative); if (!paths.Add(relative)) throw new IOException("备份路径重复。");
            string hash = value["sha256"]!.GetValue<string>(); ValidateHash(hash);
            long length = value["bytes"]!.GetValue<long>(); total = checked(total + length);
            if (length is < 0 or > MaximumFileBytes || total > MaximumCaptureBytes) throw new IOException("备份大小超过预算。");
            files.Add(new(relative, hash, length));
        }
        foreach (string pathValue in paths)
            for (string? parent = Path.GetDirectoryName(pathValue); parent is not null; parent = Path.GetDirectoryName(parent))
            { if (parent.Length == 0) break; if (paths.Contains(parent.Replace('\\', '/'))) throw new IOException("备份路径文件/目录冲突。"); }
        return new(identity, label, created, Array.AsReadOnly(files.ToArray()));
    }

    private static JsonObject Encode(ContentBackupManifest manifest)
    {
        JsonArray entries = [];
        foreach (var file in manifest.Files) entries.Add((JsonNode)new JsonObject { ["path"] = file.RelativePath, ["sha256"] = file.Sha256, ["bytes"] = file.Bytes });
        return new() { ["schema"] = 1, ["identity"] = manifest.Identity, ["label"] = manifest.Label, ["createdAt"] = manifest.CreatedAt.ToString("O", CultureInfo.InvariantCulture), ["files"] = entries };
    }

    private static async Task WriteManifestAsync(string path, ContentBackupManifest manifest, bool overwrite, CancellationToken token)
    {
        CheckLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); CheckLinks(path);
        string stage = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Encode(manifest), StorageJsonContext.Default.JsonObject);
            if (bytes.Length > 4 * 1024 * 1024) throw new IOException("备份清单超过预算。");
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            { await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
            token.ThrowIfCancellationRequested(); CheckLinks(path); File.Move(stage, path, overwrite);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    private async Task<FileStream> AcquireAsync(CancellationToken token)
    {
        CheckLinks(_root); Directory.CreateDirectory(_root); Directory.CreateDirectory(_objects); Directory.CreateDirectory(_manifests);
        CheckLinks(_objects); CheckLinks(_manifests); string path = Path.Combine(_root, ".backup.lock"); long started = Environment.TickCount64;
        while (true)
        {
            token.ThrowIfCancellationRequested(); CheckLinks(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 - started < 5000) { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }

    private static async Task<string> CopyHashAsync(Stream input, Stream output, long expected, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = new byte[81920]; long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expected - total + 1)), token).ConfigureAwait(false);
            if (read == 0) break; total += read; if (total > expected) throw new IOException("备份文件长度改变。");
            hash.AppendData(buffer.AsSpan(0, read)); await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (total != expected) throw new IOException("备份文件读取不完整。");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string[] Enumerate(string root, CancellationToken token)
    {
        List<string> files = []; Queue<string> directories = new(); directories.Enqueue(root); int visited = 0;
        while (directories.TryDequeue(out var directory))
        {
            CheckLinks(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested(); if (++visited > 16384) throw new IOException("备份目录枚举超过预算。");
                CheckLinks(path); if (Directory.Exists(path)) directories.Enqueue(path); else files.Add(path);
                if (files.Count > MaximumFiles) throw new IOException("备份文件数量超过预算。");
            }
        }
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    internal static void CheckLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("路径不能包含链接。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private void EnsureIdle() { if (!_isIdle()) throw new IOException("请等待活动游戏和任务结束。"); }
    private string ObjectPath(string hash) { ValidateHash(hash); return Path.Combine(_objects, hash + ".blob"); }
    private string ManifestPath(string identity) { ValidateIdentity(identity); return Path.Combine(_manifests, identity + ".json"); }
    private static HashSet<string> References(IEnumerable<ContentBackupManifest> manifests) => manifests.SelectMany(x => x.Files).Select(x => x.Sha256).ToHashSet(StringComparer.Ordinal);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Absolute(string path) => Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : throw new IOException("请选择绝对路径。");
    private static bool Within(string root, string path) => Nexa.Core.PathIdentity.Comparer.Equals(root, path) || path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string Relative(string root, string path) { string value = Path.GetRelativePath(root, path).Replace('\\', '/'); ValidateRelative(value); return value; }
    private static string SafePath(string root, string relative) { ValidateRelative(relative); string path = Path.GetFullPath(Path.Combine(root, relative)); if (!Within(root, path)) throw new IOException("恢复路径越界。"); return path; }
    private static void ValidateIdentity(string value) { if (!Guid.TryParseExact(value, "N", out _)) throw new IOException("备份身份无效。"); }
    private static void ValidateHash(string value) { if (value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F'))) throw new IOException("对象身份无效。"); }
    private static void ValidateRelative(string value)
    {
        if (value.Length is < 1 or > 1024 || Path.IsPathRooted(value) || value.Contains('\\') || value.Contains(':') || value.Any(char.IsControl)
            || value.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' '))) throw new IOException("备份路径无效。");
        foreach (string part in value.Split('/'))
        {
            string stem = part.Split('.')[0];
            if (part.Any(c => c is '<' or '>' or '"' or '|' or '?' or '*') || stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9') throw new IOException("备份路径包含保留文件名。");
        }
    }
    private static void ValidateRetention(int count, int days) { if (count is < 1 or > MaximumManifests || days is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(count)); }
}
