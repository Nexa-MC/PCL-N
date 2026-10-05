using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Services.Files;
using Nexa.Services.Tasks;
using Nexa.Xsr;

namespace Nexa.Services.Setup;

/// <summary>Launcher-owned data transactions. Live services only queue a move; startup performs it.</summary>
public sealed partial class StoragePreferencesService(AppFolders folders, string locatorPath, bool locationLocked = false,
    Func<bool>? isIdle = null, TaskCenterService? tasks = null) : IDisposable
{
    private const int MaxFiles = 100_000;
    private const string OwnerName = ".nexa-storage-owner";
    private const string ManifestName = ".nexa-storage-manifest";
    private static readonly HashSet<string> PathBoundJournalDirectories = new(Nexa.Core.PathIdentity.Comparer)
    {
        ".nexa-java-jobs", ".nexa-install-jobs", ".nexa-modify", ".nexa-pack-jobs", ".nexa-rename", ".nexa-content-trash"
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root = Normalize(folders.Root);
    private readonly string _locator = Path.GetFullPath(locatorPath);
    private string Pending => _locator + ".migration.json";
    private sealed record FileFact(string RelativePath, long Bytes, string Hash);
    private sealed record Scan(IReadOnlyList<string> Directories, IReadOnlyList<FileFact> Files, string Revision);
    public void Dispose() => _gate.Dispose();
    public StoragePreferencesStatus Read()
    {
        CheckLinks(Pending);
        if (!File.Exists(Pending)) return new(_root, locationLocked, false);
        if (new FileInfo(Pending).Length > 16384) throw new InvalidDataException("存储事务记录过大。");
        JsonObject record = JsonNode.Parse(File.ReadAllText(Pending)) as JsonObject ?? throw new InvalidDataException("存储事务记录无效。");
        return new(_root, locationLocked, true) { PendingTransaction = ReadTransaction(record) };
    }

    public XsrResult<StoragePreferencesStatus> ReadStatus()
    {
        try { return XsrResult.Success(Read()); }
        catch (Exception error) when (IsExpected(error)) { return XsrResult.Failure<StoragePreferencesStatus>(Failure(error)); }
    }

    public async Task<XsrResult> CancelQueuedMigrationAsync(StorageMigrationCancelCommand command, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            JsonObject record = await ReadRecordAsync(Pending, token).ConfigureAwait(false);
            string transaction = ReadTransaction(record);
            string source = Normalize(record["source"]!.GetValue<string>()), destination = Normalize(record["destination"]!.GetValue<string>());
            if (transaction != command.ExpectedTransaction || !Same(source, _root)) throw new IOException("待迁移记录已变化，请刷新后重试。");
            CheckDistinctRoots(source, destination);
            if (Same(LauncherStorageLocation.Read(_locator), destination)) throw new IOException("迁移已提交，当前数据位置不能通过取消更改。");
            string stage = StagePath(destination, transaction);
            CheckDistinctRoots(source, stage);
            if (Directory.Exists(stage))
            {
                if (IsOwned(stage, transaction)) DeleteOwned(stage, transaction);
                else TryRemoveUnmarkedEmptyStage(stage);
            }
            // A crash may publish a verified copy before the locator commits. Cancellation retains
            // that destination too; only our reserved markers and bootstrap request are retired.
            RemoveMarkers(destination, transaction);
            token.ThrowIfCancellationRequested(); File.Delete(Pending);
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (IsExpected(error)) { return XsrResult.Failure(Failure(error)); }
        finally { _gate.Release(); }
    }

    public async Task<XsrResult<StorageMigrationPreview>> PreviewMigrationAsync(StorageMigrationQuery query, CancellationToken token = default)
    {
        try
        {
            if (locationLocked) throw new IOException("数据位置由环境变量指定，不能迁移。");
            string target = ValidateTarget(_root, query.DestinationDirectory);
            Scan scan = await ScanAsync(_root, token).ConfigureAwait(false);
            return XsrResult.Success(new StorageMigrationPreview(_root, target, Hash(scan.Revision + "\0" + target), scan.Files.Count, scan.Files.Sum(file => file.Bytes)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure<StorageMigrationPreview>(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (IsExpected(error)) { return XsrResult.Failure<StorageMigrationPreview>(Failure(error)); }
    }

    public async Task<XsrResult> QueueMigrationAsync(StorageMigrationCommand command, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureIdle();
            if (File.Exists(Pending)) throw new IOException("已有待完成迁移，请重新启动启动器。");
            var preview = await PreviewMigrationAsync(new(command.DestinationDirectory), token).ConfigureAwait(false);
            if (!preview.IsSuccess) return XsrResult.Failure(preview.Error!);
            if (preview.Value!.Revision != command.ExpectedRevision) throw new IOException("数据在预览后发生变化，请重新预览。");
            EnsureIdle(); token.ThrowIfCancellationRequested();
            var record = new JsonObject
            {
                ["version"] = 1,
                ["transaction"] = Guid.NewGuid().ToString("N"),
                ["source"] = _root,
                ["destination"] = preview.Value.DestinationDirectory,
                ["revision"] = command.ExpectedRevision
            };
            await WriteRecordAsync(Pending, record, token, overwrite: false).ConfigureAwait(false);
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (IsExpected(error)) { return XsrResult.Failure(Failure(error)); }
        finally { _gate.Release(); }
    }

    /// <summary>Call before composing root consumers. Failed or interrupted migrations retain the old locator and source.</summary>
    public static async Task<XsrResult> CompletePendingMigrationAsync(string sourceRoot, string locatorPath,
        bool locationLocked = false, CancellationToken token = default)
    {
        string locator = Path.GetFullPath(locatorPath), pending = locator + ".migration.json";
        string? stage = null, destination = null, transaction = null;
        bool published = false, destinationExisted = false, committed = false;
        try
        {
            await RecoverCleanupAsync(Normalize(sourceRoot), locator, token).ConfigureAwait(false);
            CheckLinks(pending);
            if (!File.Exists(pending)) return XsrResult.Success();
            if (locationLocked) throw new IOException("环境变量锁定了数据位置，待完成迁移未执行。");
            JsonObject record = await ReadRecordAsync(pending, token).ConfigureAwait(false);
            transaction = ReadTransaction(record);
            string source = Normalize(record["source"]!.GetValue<string>());
            destination = Normalize(record["destination"]!.GetValue<string>());
            string revision = record["revision"]!.GetValue<string>();
            if (revision.Length != 64 || !revision.All(char.IsAsciiHexDigit)) throw new InvalidDataException("迁移预览身份无效。");
            CheckDistinctRoots(source, destination);
            CheckLinks(source); CheckLinks(destination); CheckLinks(locator);
            if (Same(LauncherStorageLocation.Read(locator), destination))
            {
                if (!Same(Normalize(sourceRoot), destination)) throw new IOException("已提交的位置与当前启动位置不一致。");
                RemoveMarkers(destination, transaction); File.Delete(pending);
                return XsrResult.Success();
            }
            if (!Same(Normalize(sourceRoot), source)) throw new IOException("当前数据位置与待完成迁移不一致。");
            string proposedStage = StagePath(destination, transaction);
            CheckDistinctRoots(source, proposedStage);
            stage = proposedStage;
            Scan scan = await ScanAsync(source, token).ConfigureAwait(false);
            if (Hash(scan.Revision + "\0" + destination) != revision) throw new IOException("待完成迁移的数据已变化；旧数据保留，请在设置中取消待迁移后重新预览。");
            bool recoveredPublication = IsOwned(destination, transaction);
            if (recoveredPublication)
            {
                published = true;
                await VerifyPublishedAsync(destination, transaction, scan, token).ConfigureAwait(false);
            }
            else
            {
                ValidateTarget(source, destination);
                destinationExisted = Directory.Exists(destination);
                if (Directory.Exists(stage))
                {
                    if (IsOwned(stage, transaction)) DeleteOwned(stage, transaction);
                    else if (!TryRemoveUnmarkedEmptyStage(stage)) throw new IOException("迁移暂存目录包含未知文件，已保留内容。");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(stage)!); CheckLinks(stage);
                CreateRestrictedDirectory(stage);
                WriteOwner(stage, transaction);
                foreach (string directory in scan.Directories) CreateRestrictedDirectory(Path.Combine(stage, directory));
                foreach (FileFact file in scan.Files)
                {
                    token.ThrowIfCancellationRequested();
                    string inputPath = Path.Combine(source, file.RelativePath), outputPath = Path.Combine(stage, file.RelativePath);
                    CheckLinks(inputPath); CheckLinks(outputPath);
                    await using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                    await using (var output = new FileStream(outputPath, CopyOptions()))
                    { await input.CopyToAsync(output, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
                    if (await FactAsync(stage, outputPath, token).ConfigureAwait(false) != file) throw new IOException("迁移副本校验失败。");
                    File.SetLastWriteTimeUtc(outputPath, File.GetLastWriteTimeUtc(inputPath));
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(outputPath, File.GetUnixFileMode(inputPath));
                }
                Scan after = await ScanAsync(source, token).ConfigureAwait(false);
                if (after.Revision != scan.Revision || !after.Files.SequenceEqual(scan.Files) || !after.Directories.SequenceEqual(scan.Directories))
                    throw new IOException("复制期间数据发生变化，旧数据保留。");
                var manifest = new JsonObject
                {
                    ["version"] = 1,
                    ["transaction"] = transaction,
                    ["directories"] = new JsonArray(scan.Directories.Select(path => (JsonNode)JsonValue.Create(path)!).ToArray()),
                    ["files"] = new JsonArray(scan.Files.Select(file => (JsonNode)new JsonObject { ["path"] = file.RelativePath, ["bytes"] = file.Bytes, ["sha256"] = file.Hash }).ToArray())
                };
                await WriteRecordAsync(Path.Combine(stage, ManifestName), manifest, token, maximumBytes: 32 * 1024 * 1024).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows())
                {
                    foreach (string directory in scan.Directories.OrderByDescending(path => path.Length))
                        File.SetUnixFileMode(Path.Combine(stage, directory), File.GetUnixFileMode(Path.Combine(source, directory)));
                    File.SetUnixFileMode(stage, File.GetUnixFileMode(source));
                }
                token.ThrowIfCancellationRequested(); ValidateTarget(source, destination);
                if (Directory.Exists(destination)) Directory.Delete(destination);
                Directory.Move(stage, destination); published = true;
            }
            token.ThrowIfCancellationRequested();
            LauncherStorageLocation.Save(locator, destination); committed = true;
            try { RemoveMarkers(destination, transaction); File.Delete(pending); }
            catch (Exception error) when (IsExpected(error)) { /* Locator commit is durable; bootstrap retirement retries next startup. */ }
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (IsExpected(error)) { return XsrResult.Failure(Failure(error)); }
        finally
        {
            if (!committed && transaction is not null && stage is not null)
            {
                // Leave the pending request retryable, but restore the target after a normal failure.
                try
                {
                    if (published && destination is not null && IsOwned(destination, transaction)) Directory.Move(destination, stage);
                    if (Directory.Exists(stage)) DeleteOwned(stage, transaction);
                    if (destinationExisted && destination is not null && !Directory.Exists(destination)) Directory.CreateDirectory(destination);
                }
                catch (Exception error) when (IsExpected(error)) { /* Durable owner/manifest permits startup recovery. */ }
            }
        }
    }

    private void EnsureIdle()
    {
        if (isIdle?.Invoke() != true || tasks?.ReadEntries().Any(entry => !entry.IsTerminal) == true)
            throw new IOException("请先结束游戏和后台任务，再执行存储操作。");
    }
    private static async Task<Scan> ScanAsync(string root, CancellationToken token)
    {
        CheckLinks(root);
        if (!Directory.Exists(root)) throw new IOException("当前数据目录不存在。");
        RejectPathBoundStorage(root);
        var directories = new List<string>(); var files = new List<FileFact>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested(); CheckLinks(entry);
                string relative = Path.GetRelativePath(root, entry);
                if (relative is OwnerName or ManifestName) throw new IOException("数据目录包含保留的迁移记录。");
                if (Directory.Exists(entry)) { RejectPathBoundStorage(entry); directories.Add(relative); pending.Push(entry); }
                else files.Add(await FactAsync(root, entry, token).ConfigureAwait(false));
                if (directories.Count + files.Count > MaxFiles) throw new IOException("数据目录超过迁移条目限制。");
            }
        }
        directories.Sort(StringComparer.Ordinal); files.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        // Logs are flushed/rotated during shutdown; all current bytes are copied on startup.
        string fingerprint = string.Join('\n', directories.Where(path => !IsLog(path)).Select(path => "D\0" + path)
            .Concat(files.Where(file => !IsLog(file.RelativePath)).Select(file => file.RelativePath + "\0" + file.Bytes + "\0" + file.Hash)));
        return new(directories, files, Hash(fingerprint));
    }
    private static void RejectPathBoundStorage(string directory)
    {
        string name = Path.GetFileName(directory);
        bool instanceRecovery = Nexa.Core.PathIdentity.Comparer.Equals(name, "Recovery")
            && Nexa.Core.PathIdentity.Comparer.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "Nexa");
        bool loaderReceipts = Nexa.Core.PathIdentity.Comparer.Equals(name, "loader")
            && Nexa.Core.PathIdentity.Comparer.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), ".task");
        if ((PathBoundJournalDirectories.Contains(name) || instanceRecovery || loaderReceipts) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("数据目录包含绑定原路径的 Minecraft/Java 安装或恢复记录，不能通过启动器数据迁移移动。请将游戏和 Java 存储保留在启动器数据目录之外。");
    }
    private static bool IsLog(string path) => path == FolderNames.Logs || path.StartsWith(FolderNames.Logs + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static async Task<FileFact> FactAsync(string root, string path, CancellationToken token)
    {
        CheckLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
        long size = input.Length;
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
        if (size != input.Length) throw new IOException("读取期间文件大小发生变化。");
        return new(Path.GetRelativePath(root, path), size, hash);
    }
    private static async Task VerifyPublishedAsync(string root, string transaction, Scan source, CancellationToken token)
    {
        JsonObject manifest = await ReadRecordAsync(Path.Combine(root, ManifestName), token, 32 * 1024 * 1024).ConfigureAwait(false);
        if (ReadTransaction(manifest) != transaction || manifest["files"] is not JsonArray files || files.Count > MaxFiles
            || manifest["directories"] is not JsonArray directories || directories.Count > MaxFiles) throw new InvalidDataException("迁移副本记录无效。");
        HashSet<string> expected = new(Nexa.Core.PathIdentity.Comparer);
        var facts = new List<FileFact>();
        foreach (JsonNode? node in files)
        {
            string relative = node!["path"]!.GetValue<string>();
            string path = Nexa.Core.PathIdentity.Contained(root, relative);
            if (!expected.Add(path)) throw new InvalidDataException("迁移副本记录重复。");
            var fact = await FactAsync(root, path, token).ConfigureAwait(false);
            if (fact.Bytes != node["bytes"]!.GetValue<long>() || fact.Hash != node["sha256"]!.GetValue<string>()) throw new IOException("迁移副本已变化。");
            facts.Add(fact);
        }
        CheckTreeLinks(root);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            if (Path.GetFileName(path) is not (OwnerName or ManifestName) && !expected.Contains(path)) throw new IOException("迁移副本包含未预览文件。");
        var expectedDirectories = directories.Select(node => node?.GetValue<string>() ?? throw new InvalidDataException("迁移目录记录无效。"))
            .Order(StringComparer.Ordinal).ToArray();
        var actualDirectories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.Ordinal).ToArray();
        facts.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        if (!expectedDirectories.SequenceEqual(actualDirectories) || !expectedDirectories.SequenceEqual(source.Directories) || !facts.SequenceEqual(source.Files))
            throw new IOException("迁移副本与当前数据不一致。");
    }
    private static string ValidateTarget(string source, string target)
    {
        if (!Path.IsPathFullyQualified(target)) throw new IOException("请选择完整的数据目录路径。");
        target = Normalize(target); CheckDistinctRoots(source, target); CheckLinks(target);
        if (File.Exists(target) || Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("请选择空文件夹，已有数据不会被覆盖。");
        return target;
    }
    private static void CheckDistinctRoots(string source, string target)
    {
        if (Same(source, target) || Same(target, Path.GetPathRoot(target)) || IsInside(source, target) || IsInside(target, source))
            throw new IOException("数据目录不能相同、互相包含或使用磁盘根目录。");
    }
    private static bool IsInside(string parent, string child) => child.StartsWith(parent + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Same(string? left, string? right) => Nexa.Core.PathIdentity.Comparer.Equals(left, right);
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static string StagePath(string destination, string transaction) => Path.Combine(Path.GetDirectoryName(destination)!, ".nexa-storage-migrate-" + transaction);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string ReadTransaction(JsonObject record)
    {
        string? transaction = record["transaction"]?.GetValue<string>();
        if (record["version"]?.GetValue<int>() != 1 || transaction?.Length != 32 || !Guid.TryParseExact(transaction, "N", out _)) throw new InvalidDataException("存储事务记录无效。");
        return transaction;
    }
    private static bool IsOwned(string path, string transaction)
    {
        CheckLinks(path);
        string owner = Path.Combine(path, OwnerName); CheckLinks(owner);
        return File.Exists(owner) && new FileInfo(owner).Length == 32 && File.ReadAllText(owner) == transaction;
    }
    private static void RemoveMarkers(string root, string transaction)
    {
        if (!IsOwned(root, transaction)) return;
        CheckLinks(Path.Combine(root, ManifestName));
        File.Delete(Path.Combine(root, ManifestName)); File.Delete(Path.Combine(root, OwnerName));
    }
    private static void DeleteOwned(string root, string transaction)
    {
        if (!IsOwned(root, transaction)) throw new IOException("暂存目录不属于当前迁移，未删除任何内容。");
        CheckTreeLinks(root);
        if (!OperatingSystem.IsWindows())
        {
            foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
                File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Directory.Delete(root, recursive: true);
    }
    private static void CreateRestrictedDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static FileStreamOptions CopyOptions()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 81920, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
    private static void WriteOwner(string root, string transaction)
    {
        string path = Path.Combine(root, OwnerName); CheckLinks(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        file.Write(Encoding.ASCII.GetBytes(transaction)); file.Flush(true);
    }
    private static bool TryRemoveUnmarkedEmptyStage(string stage)
    {
        CheckLinks(stage);
        string[] entries = Directory.GetFileSystemEntries(stage);
        if (entries.Length == 1 && Path.GetFileName(entries[0]) == OwnerName && File.Exists(entries[0]))
        { CheckLinks(entries[0]); File.Delete(entries[0]); }
        else if (entries.Length != 0) return false;
        Directory.Delete(stage); return true;
    }
    private static void CheckTreeLinks(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory))
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) { CheckLinks(entry); if (Directory.Exists(entry)) pending.Push(entry); }
    }
    private static void CheckLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("存储路径不能包含符号链接。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static bool IsExpected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or FormatException or OverflowException or NullReferenceException;
    private static XsrError Failure(Exception error) => new(XsrErrorKind.Rejected, XsrSemanticId.Parse("setup.storage.failed"), error.Message);
    private static async Task<JsonObject> ReadRecordAsync(string path, CancellationToken token, int budget = 16384)
    {
        CheckLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (input.Length > budget) throw new InvalidDataException("存储事务记录过大。");
        byte[] bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (input.ReadByte() != -1) throw new IOException("存储事务记录发生变化。");
        return JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("存储事务记录无效。");
    }
    private static async Task WriteRecordAsync(string path, JsonObject record, CancellationToken token, bool overwrite = true, int maximumBytes = 16384)
    {
        CheckLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, StorageJsonContext.Default.JsonObject);
                if (bytes.Length > maximumBytes) throw new InvalidDataException("存储事务记录超过大小限制。");
                await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
            }
            token.ThrowIfCancellationRequested(); CheckLinks(path); File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal sealed partial class StorageJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
