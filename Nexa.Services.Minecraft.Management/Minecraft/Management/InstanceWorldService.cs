using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Management;

public static partial class InstanceWorldService
{
    private const int Limit = 8 * 1024 * 1024;
    private const string LockName = ".nexa-world-readonly";
    private static readonly string[] PackLists = ["Enabled", "Disabled"];

    public static async Task<InstanceWorldMetadata> ReadAsync(InstanceWorldMetadataQuery query, CancellationToken token = default)
    {
        if (!MinecraftVersionPaths.IsSafeReference(query.WorldName) || query.WorldName.StartsWith(".nexa-", StringComparison.Ordinal)) throw new InvalidDataException("世界名称无效。");
        var snapshot = await InstanceManagementService.ReadAsync(new(query.InstanceDirectory), token).ConfigureAwait(false);
        string directory = Path.Combine(snapshot.GameDirectory, "saves", query.WorldName); RecoveryBlobStore.CheckLinks(directory);
        return await ReadMetadataAsync(directory, token: token).ConfigureAwait(false);
    }

    internal static async Task<InstanceWorldMetadata> ReadMetadataAsync(string directory, Nexa.Services.Files.ArchiveReadBudget? budget = null, CancellationToken token = default)
    {
        var (root, revision) = await ReadLevelAsync(directory, budget, token).ConfigureAwait(false);
        var data = Data(root); var version = data.Children.SingleOrDefault(tag => tag.Name == "Version" && tag.Type == 10);
        var packs = data.Children.SingleOrDefault(tag => tag.Name == "DataPacks" && tag.Type == 10);
        List<InstanceWorldDataPack> entries = [];
        foreach (var (key, enabled) in new[] { ("Enabled", true), ("Disabled", false) })
        {
            var list = packs?.Children.SingleOrDefault(tag => tag.Name == key && tag.Type == 9);
            if (list is not null && list.ListType != 8 && !(list.ListType == 0 && list.Children.Count == 0)) throw new InvalidDataException("世界数据包列表无效。");
            foreach (var item in list?.Children.Take(256) ?? [])
            {
                string? id = StringValue(item);
                if (id is null || id.Length > 512) continue;
                entries.Add(new(id, id.StartsWith("file/", StringComparison.Ordinal) ? id[5..] : id, enabled, !id.StartsWith("file/", StringComparison.Ordinal)));
            }
        }
        string packDirectory = Path.Combine(directory, "datapacks"); RecoveryBlobStore.CheckLinks(packDirectory);
        if (Directory.Exists(packDirectory))
            foreach (var file in new DirectoryInfo(packDirectory).EnumerateFileSystemInfos().Take(256))
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && !entries.Any(item => item.Id == "file/" + file.Name)
                    && file.Name is not ".nexa-datapack-trash" && !file.Name.StartsWith(".nexa-", StringComparison.Ordinal))
                    entries.Add(new("file/" + file.Name, file.Name, false, false));
        string trash = Path.Combine(packDirectory, ".nexa-datapack-trash"); RecoveryBlobStore.CheckLinks(trash);
        if (Directory.Exists(trash))
            foreach (var file in new DirectoryInfo(trash).EnumerateFileSystemInfos().Take(100))
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && file.Name.Length > 33 && file.Name[32] == '-'
                    && Guid.TryParseExact(file.Name[..32], "N", out _) && MinecraftVersionPaths.IsSafeReference(file.Name[33..]))
                    entries.Add(new("trash/" + file.Name, file.Name[33..], false, false) { Trashed = true, TrashName = file.Name });
        long size = 0; bool complete = true;
        try { foreach (var file in Inventory(directory, token)) size = checked(size + file.Length); }
        catch (InvalidDataException) { complete = false; }
        long? played = Number(data, "LastPlayed"); DateTimeOffset? lastPlayed = null;
        if (played is not null)
            try { lastPlayed = DateTimeOffset.FromUnixTimeMilliseconds(played.Value); } catch (ArgumentOutOfRangeException) { }
        var generation = data.Children.SingleOrDefault(tag => tag.Name == "WorldGenSettings" && tag.Type == 10);
        return new(data.String("LevelName") ?? Path.GetFileName(directory), version?.String("Name") ?? "", Int(data, "DataVersion"), lastPlayed,
            generation is null ? Number(data, "RandomSeed") : Number(generation, "seed"), Int(data, "GameType"), Int(data, "Difficulty"), Number(data, "hardcore") == 1,
            size, complete, revision, File.Exists(Path.Combine(directory, LockName)), entries.DistinctBy(item => item.Id).ToArray())
        { DataPacksSupported = SupportsDataPacks(root) };
    }

    public static Task<XsrResult> CopyAsync(InstanceWorldCopyCommand command, XsrStateStore store, Func<string, IDisposable>? acquireSessionLock = null, CancellationToken token = default) => ExecuteAsync(
        command.InstanceDirectory, command.WorldName, command.ExpectedRevision, store, false, async (snapshot, world, _, ct) =>
        {
            if (!MinecraftVersionPaths.IsSafeReference(command.DestinationName) || command.DestinationName.StartsWith(".nexa-", StringComparison.Ordinal)) throw new InvalidDataException("世界副本名称无效。");
            string target = Path.Combine(snapshot.GameDirectory, "saves", command.DestinationName);
            RecoveryBlobStore.CheckLinks(target); if (Path.Exists(target)) throw new IOException("同名世界已存在。");
            string stage = Path.Combine(snapshot.GameDirectory, "saves", ".nexa-world-copy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                var files = Inventory(world, ct); long total = 0;
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested(); total = checked(total + file.Length);
                    if (total > 64L * 1024 * 1024 * 1024) throw new InvalidDataException("世界复制超过 64 GiB 预算。");
                    if (file.Relative is "session.lock" or LockName) continue;
                    string destination = Path.Combine(stage, file.Relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await CopyFileAsync(file.Path, destination, ct).ConfigureAwait(false);
                }
                foreach (var file in files)
                {
                    RecoveryBlobStore.CheckLinks(file.Path); var info = new FileInfo(file.Path);
                    if (!info.Exists || info.Length != file.Length || info.LastWriteTimeUtc.Ticks != file.Modified) throw new IOException("复制期间世界内容变化，未发布副本。");
                }
                ct.ThrowIfCancellationRequested(); RecoveryBlobStore.CheckLinks(target); Directory.Move(stage, target);
            }
            finally { DeleteOwnedTree(stage); }
        }, acquireSessionLock, token);

    public static Task<XsrResult> BackupAsync(InstanceWorldBackupCommand command, XsrStateStore store,
        Func<string, CancellationToken, Task> capture, Func<string, IDisposable>? acquireSessionLock = null, CancellationToken token = default) => ExecuteAsync(
        command.InstanceDirectory, command.WorldName, command.ExpectedRevision, store, true,
        (_, world, _, ct) => capture(world, ct), acquireSessionLock, token);

    public static Task<XsrResult> SetLockAsync(InstanceWorldLockCommand command, XsrStateStore store, Func<string, IDisposable>? acquireSessionLock = null, CancellationToken token = default) => ExecuteAsync(
        command.InstanceDirectory, command.WorldName, command.ExpectedRevision, store, true, (snapshot, world, _, ct) =>
        {
            string path = Path.Combine(world, LockName); RecoveryBlobStore.CheckLinks(path); ct.ThrowIfCancellationRequested();
            if (command.Locked)
            {
                if (!File.Exists(path)) { using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write("NexaCL world edit lock v1\n"u8); output.Flush(true); }
            }
            else File.Delete(path);
            return Task.CompletedTask;
        }, acquireSessionLock, token);

    public static Task<XsrResult> SetDataPackEnabledAsync(InstanceWorldDataPackCommand command, XsrStateStore store, Func<string, IDisposable>? acquireSessionLock = null, CancellationToken token = default) => ExecuteAsync(
        command.InstanceDirectory, command.WorldName, command.ExpectedRevision, store, false, async (_, world, root, ct) =>
        {
            RequireDataPacks(root);
            var data = Data(root); var packs = data.Children.SingleOrDefault(tag => tag.Name == "DataPacks");
            if (packs is null) { packs = new(10, "DataPacks", []); data.Children.Add(packs); }
            if (packs.Type != 10 || command.Id.Length is 0 or > 512 || command.Id.Any(char.IsControl)) throw new InvalidDataException("数据包标识无效。");
            if (command.Id == "vanilla" && !command.Enabled) throw new InvalidDataException("不能停用 Minecraft 自带数据包。");
            if (command.Id.StartsWith("file/", StringComparison.Ordinal))
            {
                string name = command.Id[5..]; if (!MinecraftVersionPaths.IsSafeReference(name)) throw new InvalidDataException("数据包名称无效。");
                string path = Path.Combine(world, "datapacks", name); RecoveryBlobStore.CheckLinks(path);
                if (!Path.Exists(path)) throw new IOException("数据包已不存在。");
            }
            else if (!packs.Children.Any(list => list.Type == 9 && list.Children.Any(item => StringValue(item) == command.Id))) throw new InvalidDataException("内置数据包不属于此世界。");
            foreach (string key in PackLists)
            {
                var list = packs.Children.SingleOrDefault(tag => tag.Name == key);
                if (list is null) { list = new(9, key, []) { ListType = 8 }; packs.Children.Add(list); }
                if (list.Type != 9 || list.ListType is not (0 or 8) || list.Children.Count > 256) throw new InvalidDataException("数据包列表无效。");
                list.ListType = 8; list.Children.RemoveAll(item => StringValue(item) == command.Id);
                if ((key == "Enabled") == command.Enabled) { var holder = new ServerNbt(10, "", []); holder.SetString("", command.Id); list.Children.Add(holder.Children[0]); }
            }
            await WriteLevelAsync(world, root, command.ExpectedRevision, ct).ConfigureAwait(false);
        }, acquireSessionLock, token);

    public static Task<XsrResult> ImportDataPackAsync(InstanceWorldDataPackImportCommand command, XsrStateStore store, Func<string, IDisposable>? acquireSessionLock = null, CancellationToken token = default) => ExecuteAsync(
        command.InstanceDirectory, command.WorldName, command.ExpectedRevision, store, false, async (_, world, root, ct) =>
        {
            RequireDataPacks(root);
            string source = Path.GetFullPath(command.SourcePath); RecoveryBlobStore.CheckLinks(source);
            if (!File.Exists(source) || !source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择 ZIP 数据包。");
            using (var input = File.OpenRead(source))
            {
                if (input.Length > 512L * 1024 * 1024) throw new InvalidDataException("数据包超过 512 MiB。");
                using var archive = new ZipArchive(input, ZipArchiveMode.Read);
                var metadata = archive.Entries.Where(entry => entry.FullName == "pack.mcmeta").ToArray();
                if (archive.Entries.Count > 10000 || metadata.Length != 1 || metadata[0].Length > 256 * 1024) throw new InvalidDataException("数据包缺少唯一 pack.mcmeta。");
                using var body = metadata[0].Open(); using var bytes = new MemoryStream(); await CopyBoundedAsync(body, bytes, 256 * 1024, ct).ConfigureAwait(false);
                if (JsonNode.Parse(bytes.ToArray())?["pack"] is not JsonObject) throw new InvalidDataException("数据包元数据无效。");
            }
            string name = Path.GetFileName(source); if (!MinecraftVersionPaths.IsSafeReference(name)) throw new InvalidDataException("数据包名称无效。");
            string directory = Path.Combine(world, "datapacks"); RecoveryBlobStore.CheckLinks(directory); Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, name), stage = Path.Combine(directory, ".nexa-datapack-" + Guid.NewGuid().ToString("N"));
            RecoveryBlobStore.CheckLinks(destination); if (Path.Exists(destination)) throw new IOException("同名数据包已存在。");
            try { await CopyFileAsync(source, stage, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); File.Move(stage, destination, false); }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        }, acquireSessionLock, token);

    public static Task<XsrResult> RemoveDataPackAsync(InstanceWorldDataPackRemoveCommand command, XsrStateStore store, Func<string, IDisposable>? acquireSessionLock = null, CancellationToken token = default) => ExecuteAsync(
        command.InstanceDirectory, command.WorldName, command.ExpectedRevision, store, false, (_, world, root, ct) =>
        {
            RequireDataPacks(root);
            if (!MinecraftVersionPaths.IsSafeReference(command.Name)) throw new InvalidDataException("数据包名称无效。");
            var packs = Data(root).Children.SingleOrDefault(tag => tag.Name == "DataPacks");
            if (packs?.Children.Where(tag => tag.Name == "Enabled").Any(list => list.Children.Any(item => StringValue(item) == "file/" + command.Name)) == true)
                throw new InvalidDataException("请先停用数据包，再移除。");
            string source = Path.Combine(world, "datapacks", command.Name), trash = Path.Combine(world, "datapacks", ".nexa-datapack-trash");
            RecoveryBlobStore.CheckLinks(source); RecoveryBlobStore.CheckLinks(trash); Directory.CreateDirectory(trash); ct.ThrowIfCancellationRequested();
            string destination = Path.Combine(trash, Guid.NewGuid().ToString("N") + "-" + command.Name);
            if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination, false);
            return Task.CompletedTask;
        }, acquireSessionLock, token);

    public static Task<XsrResult> RestoreDataPackAsync(InstanceWorldDataPackRestoreCommand command, XsrStateStore store, Func<string, IDisposable>? acquireSessionLock = null, CancellationToken token = default) => ExecuteAsync(
        command.InstanceDirectory, command.WorldName, command.ExpectedRevision, store, false, (snapshot, world, root, ct) =>
        {
            RequireDataPacks(root);
            if (!MinecraftVersionPaths.IsSafeReference(command.TrashName) || command.TrashName.Length <= 33 || command.TrashName[32] != '-'
                || !Guid.TryParseExact(command.TrashName[..32], "N", out _) || !MinecraftVersionPaths.IsSafeReference(command.TrashName[33..]))
                throw new InvalidDataException("数据包回收记录无效。");
            string source = Path.Combine(world, "datapacks", ".nexa-datapack-trash", command.TrashName), target = Path.Combine(world, "datapacks", command.TrashName[33..]);
            RecoveryBlobStore.CheckLinks(source); RecoveryBlobStore.CheckLinks(target);
            if (Path.Exists(target)) throw new IOException("同名数据包已存在，未覆盖内容。");
            ct.ThrowIfCancellationRequested(); if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target, false);
            return Task.CompletedTask;
        }, acquireSessionLock, token);

    internal static async Task<XsrResult> ExecuteAsync(string instance, string name, string revision, XsrStateStore store, bool allowLocked,
        Func<InstanceManagementSnapshot, string, ServerNbt, CancellationToken, Task> execute, Func<string, IDisposable>? acquireSessionLock, CancellationToken token)
    {
        try
        {
            if (!MinecraftVersionPaths.IsSafeReference(name) || name.StartsWith(".nexa-", StringComparison.Ordinal)) throw new InvalidDataException("世界名称无效。");
            var snapshot = await InstanceManagementService.ReadAsync(new(instance), token).ConfigureAwait(false);
            using var lease = await InstanceRecoveryOperationGate.EnterRestoreAsync(Directory.GetParent(snapshot.InstanceDirectory)!.Parent!.FullName, token).ConfigureAwait(false);
            InstanceContentTrash.RejectRunning(snapshot, store, token);
            string directory = Path.Combine(snapshot.GameDirectory, "saves", name); RecoveryBlobStore.CheckLinks(directory);
            if (!allowLocked && File.Exists(Path.Combine(directory, LockName))) throw new IOException("世界已锁定，请先解除编辑保护。");
            using var session = AcquireSessionLock(directory, acquireSessionLock);
            var (root, actual) = await ReadLevelAsync(directory, token: token).ConfigureAwait(false);
            if (actual != revision) throw new IOException("世界资料已变化，请刷新后重试。");
            await execute(snapshot, directory, root, token).ConfigureAwait(false); return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OverflowException or System.Text.Json.JsonException)
        { return XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
    }

    private static IDisposable? AcquireSessionLock(string world, Func<string, IDisposable>? acquireSessionLock)
    {
        string path = Path.Combine(world, "session.lock"); RecoveryBlobStore.CheckLinks(path);
        if (!File.Exists(path)) return null;
        if (acquireSessionLock is not null) return acquireSessionLock(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            // Java uses a process-level advisory file lock on Unix. macOS .NET has no
            // FileStream.Lock support; conservative refusal avoids an unverified write.
            if (OperatingSystem.IsMacOS()) throw new IOException("此平台无法验证 session.lock，请关闭游戏并移走过期锁文件后重试。");
            file.Lock(0, long.MaxValue); return file;
        }
        catch { file.Dispose(); throw; }
    }

    private static ServerNbt Data(ServerNbt root) => root.Children.SingleOrDefault(tag => tag.Name == "Data" && tag.Type == 10) ?? throw new InvalidDataException("世界 level.dat 缺少 Data 根节点。");
    private static bool SupportsDataPacks(ServerNbt root) => Int(Data(root), "DataVersion") is >= 1519;
    private static void RequireDataPacks(ServerNbt root)
    { if (!SupportsDataPacks(root)) throw new InvalidDataException("此世界的数据版本不支持 Minecraft 数据包。"); }
    private static int? Int(ServerNbt root, string name) => Number(root, name) is { } value && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;
    private static long? Number(ServerNbt root, string name)
    {
        var tag = root.Children.SingleOrDefault(tag => tag.Name == name);
        return tag?.Type switch { 1 => (sbyte)tag.Payload[0], 2 => BinaryPrimitives.ReadInt16BigEndian(tag.Payload), 3 => BinaryPrimitives.ReadInt32BigEndian(tag.Payload), 4 => BinaryPrimitives.ReadInt64BigEndian(tag.Payload), _ => null };
    }
    private static string? StringValue(ServerNbt tag)
    {
        if (tag.Type != 8) return null;
        var root = new ServerNbt(10, "", []); root.Children.Add(new(8, "value", tag.Payload)); return root.String("value");
    }
    private static async Task<(ServerNbt Root, string Revision)> ReadLevelAsync(string directory, Nexa.Services.Files.ArchiveReadBudget? budget = null, CancellationToken token = default)
    {
        string path = Path.Combine(directory, "level.dat"); RecoveryBlobStore.CheckLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var encoded = new MemoryStream(); await CopyBoundedAsync(input, encoded, Math.Min(Limit, budget?.Remaining ?? Limit), token).ConfigureAwait(false);
        budget?.Consume((int)encoded.Length);
        byte[] bytes = encoded.ToArray(); string revision = Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes.Length > 2 && bytes[0] == 31 && bytes[1] == 139)
        {
            encoded.Position = 0; using var gzip = new GZipStream(encoded, CompressionMode.Decompress); using var raw = new MemoryStream();
            await CopyBoundedAsync(gzip, raw, Math.Min(Limit, budget?.Remaining ?? Limit), token).ConfigureAwait(false); budget?.Consume((int)raw.Length); bytes = raw.ToArray();
        }
        return (ServerNbt.Parse(bytes), revision);
    }
    private static async Task WriteLevelAsync(string directory, ServerNbt root, string revision, CancellationToken token)
    {
        string path = Path.Combine(directory, "level.dat"), stage = Path.Combine(directory, ".nexa-level-" + Guid.NewGuid().ToString("N")), backup = Path.Combine(directory, "level.dat.nexa-backup");
        RecoveryBlobStore.CheckLinks(path); RecoveryBlobStore.CheckLinks(backup);
        try
        {
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) await gzip.WriteAsync(root.Serialize(), token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
            }
            if ((await ReadLevelAsync(directory, token: token).ConfigureAwait(false)).Revision != revision) throw new IOException("世界元数据已变化。");
            token.ThrowIfCancellationRequested(); File.Replace(stage, path, backup, true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    private sealed record WorldFile(string Path, string Relative, long Length, long Modified);
    private static List<WorldFile> Inventory(string root, CancellationToken token)
    {
        List<WorldFile> files = []; Stack<(string Path, int Depth)> directories = new(); directories.Push((root, 0));
        while (directories.TryPop(out var directory))
        {
            if (directory.Depth > 32) throw new InvalidDataException("世界目录层级超过限制。");
            foreach (var entry in new DirectoryInfo(directory.Path).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested(); RecoveryBlobStore.CheckLinks(entry.FullName);
                if (entry is DirectoryInfo) directories.Push((entry.FullName, directory.Depth + 1));
                else if (entry is FileInfo file)
                {
                    if (files.Count == 10000 || file.Length > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("世界文件超过扫描预算。");
                    files.Add(new(file.FullName, Path.GetRelativePath(root, file.FullName), file.Length, file.LastWriteTimeUtc.Ticks));
                }
            }
        }
        return files;
    }
    private static async Task CopyFileAsync(string source, string target, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(source); RecoveryBlobStore.CheckLinks(target);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await CopyBoundedAsync(input, output, 8L * 1024 * 1024 * 1024, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
    }
    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        byte[] buffer = new byte[81920]; long count = 0;
        while (true) { int read = await input.ReadAsync(buffer, token).ConfigureAwait(false); if (read == 0) break; count += read; if (count > limit) throw new InvalidDataException("内容超过读取预算。"); await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); }
    }
    internal static void DeleteOwnedTree(string path)
    {
        if (!Directory.Exists(path)) return;
        RecoveryBlobStore.CheckLinks(path);
        foreach (string child in Directory.EnumerateFileSystemEntries(path))
        { RecoveryBlobStore.CheckLinks(child); if (Directory.Exists(child)) DeleteOwnedTree(child); else File.Delete(child); }
        Directory.Delete(path);
    }
}
