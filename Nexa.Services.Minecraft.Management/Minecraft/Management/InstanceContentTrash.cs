using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Management;

public static class InstanceContentTrash
{
    private const string TrashFolder = ".nexa-content-trash";
    private static readonly HashSet<string> Pages = new(StringComparer.Ordinal) { "mods", "resourcepacks", "shaderpacks", "schematics", "saves", "screenshots" };

    public static Task<XsrResult> RemoveAsync(InstanceContentRemoveCommand command, XsrStateStore store, CancellationToken token = default) => RemoveBatchAsync([command], store, token: token);

    internal static async Task<XsrResult> RemoveBatchAsync(IReadOnlyList<InstanceContentRemoveCommand> commands, XsrStateStore store, Func<CancellationToken, Task>? validate = null, CancellationToken token = default)
    {
        try
        {
            if (commands.Count is 0 or > 513 || commands.Any(item => item.InstanceDirectory != commands[0].InstanceDirectory)
                || commands.DistinctBy(item => (item.PageId, item.Name)).Count() != commands.Count) throw new InvalidDataException("移除列表无效。");
            var command = commands[0];
            var snapshot = await InstanceManagementService.ReadAsync(new(command.InstanceDirectory), token).ConfigureAwait(false);
            using var lease = await InstanceRecoveryOperationGate.EnterRestoreAsync(Directory.GetParent(snapshot.InstanceDirectory)!.Parent!.FullName, token).ConfigureAwait(false);
            snapshot = await InstanceManagementService.ReadAsync(new(command.InstanceDirectory), token).ConfigureAwait(false);
            RejectRunning(snapshot, store, token);
            if (validate is not null) await validate(token).ConfigureAwait(false);
            foreach (var item in commands) ValidateRemoval(item, snapshot);
            foreach (var removal in commands) RemoveOne(removal, snapshot, token);
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        { return XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
    }

    private static void ValidateRemoval(InstanceContentRemoveCommand command, InstanceManagementSnapshot snapshot)
    {
        if (!Pages.Contains(command.PageId) || !MinecraftVersionPaths.IsSafeReference(command.Name)) throw new InvalidDataException("请选择有效的内容项。");
        string directory = snapshot.Pages.SingleOrDefault(page => page.Id == command.PageId)?.Directory ?? throw new InvalidDataException("当前版本不提供此内容页。");
        string source = Path.Combine(directory, command.Name);
        RecoveryBlobStore.CheckLinks(source);
        FileSystemInfo item = command.IsDirectory ? new DirectoryInfo(source) : new FileInfo(source);
        if (!item.Exists || item.LastWriteTimeUtc.Ticks != command.ExpectedModifiedUtcTicks
            || item is FileInfo file && file.Length != command.ExpectedSize)
            throw new IOException("内容已变化，请刷新后再移除。");
    }
    private static string RemoveOne(InstanceContentRemoveCommand command, InstanceManagementSnapshot snapshot, CancellationToken token)
    {
        ValidateRemoval(command, snapshot);
        string source = Path.Combine(snapshot.GameDirectory, command.PageId, command.Name);
        string trash = Path.Combine(snapshot.GameDirectory, TrashFolder);
        RecoveryBlobStore.CheckLinks(trash);
        Directory.CreateDirectory(trash);
        string id = Guid.NewGuid().ToString("N"), transaction = Path.Combine(trash, id);
        Directory.CreateDirectory(transaction);
        JsonObject record = new()
        {
            ["version"] = 1,
            ["instance"] = snapshot.InstanceDirectory,
            ["game"] = snapshot.GameDirectory,
            ["page"] = command.PageId,
            ["name"] = command.Name,
            ["directory"] = command.IsDirectory,
            ["removedAt"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(record.ToJsonString());
        using (var journal = new FileStream(Path.Combine(transaction, "record.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { journal.Write(bytes); journal.Flush(true); }
        token.ThrowIfCancellationRequested();
        Move(source, Path.Combine(transaction, "content"), command.IsDirectory);
        return transaction;
    }

    /// <summary>Publishes a verified staged pack and journals the original using the same content lifecycle.</summary>
    public static async Task<XsrResult> ReplacePackAsync(InstanceContentRemoveCommand command, string stagedPath, string newName,
        XsrStateStore store, CancellationToken token = default)
    {
        try
        {
            if (command.PageId is not ("resourcepacks" or "shaderpacks") || command.IsDirectory
                || !MinecraftVersionPaths.IsSafeReference(newName) || !newName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择有效的资源包或光影包。");
            var snapshot = await InstanceManagementService.ReadAsync(new(command.InstanceDirectory), token).ConfigureAwait(false);
            using var lease = await InstanceRecoveryOperationGate.EnterRestoreAsync(Directory.GetParent(snapshot.InstanceDirectory)!.Parent!.FullName, token).ConfigureAwait(false);
            snapshot = await InstanceManagementService.ReadAsync(new(command.InstanceDirectory), token).ConfigureAwait(false);
            RejectRunning(snapshot, store, token); ValidateRemoval(command, snapshot);
            var stage = new FileInfo(Path.GetFullPath(stagedPath));
            if (stage.Directory is not { } staging || !staging.Name.StartsWith(".nexa-resource-", StringComparison.Ordinal)
                || !Nexa.Core.PathIdentity.Comparer.Equals(staging.Parent?.FullName, snapshot.GameDirectory))
                throw new InvalidDataException("下载暂存文件不属于此游戏目录。");
            RecoveryBlobStore.CheckLinks(stage.FullName);
            if (!stage.Exists || stage.Length is <= 0 or > 2L * 1024 * 1024 * 1024) throw new IOException("下载暂存文件不可用。");
            string target = Path.Combine(snapshot.GameDirectory, command.PageId, newName);
            string original = Path.Combine(snapshot.GameDirectory, command.PageId, command.Name);
            RecoveryBlobStore.CheckLinks(target);
            if (!Nexa.Core.PathIdentity.Comparer.Equals(target, original) && Path.Exists(target)) throw new IOException("新版本文件已存在，未覆盖任何内容。");
            token.ThrowIfCancellationRequested();
            string transaction = RemoveOne(command, snapshot, token);
            try { File.Move(stage.FullName, target, false); }
            catch
            {
                RecoveryBlobStore.CheckLinks(original); RecoveryBlobStore.CheckLinks(transaction);
                File.Move(Path.Combine(transaction, "content"), original, false);
                throw;
            }
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        { return XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
    }

    public static async Task<XsrResult> RestoreAsync(InstanceContentRestoreCommand command, XsrStateStore store, CancellationToken token = default)
    {
        try
        {
            if (!Guid.TryParseExact(command.TrashId, "N", out _)) throw new InvalidDataException("回收记录无效。");
            var snapshot = await InstanceManagementService.ReadAsync(new(command.InstanceDirectory), token).ConfigureAwait(false);
            using var lease = await InstanceRecoveryOperationGate.EnterRestoreAsync(Directory.GetParent(snapshot.InstanceDirectory)!.Parent!.FullName, token).ConfigureAwait(false);
            snapshot = await InstanceManagementService.ReadAsync(new(command.InstanceDirectory), token).ConfigureAwait(false);
            RejectRunning(snapshot, store, token);
            string transaction = Path.Combine(snapshot.GameDirectory, TrashFolder, command.TrashId);
            var record = ReadRecord(transaction, snapshot.InstanceDirectory, snapshot.GameDirectory);
            string page = record["page"]!.GetValue<string>(), name = record["name"]!.GetValue<string>();
            // An installed loader may have changed since removal; the fixed content-directory mapping remains valid.
            string directory = Path.Combine(snapshot.GameDirectory, page), destination = Path.Combine(directory, name);
            string source = Path.Combine(transaction, "content");
            RecoveryBlobStore.CheckLinks(destination); RecoveryBlobStore.CheckLinks(source);
            if (Path.Exists(destination)) throw new IOException("同名内容已存在，未覆盖任何文件。");
            Directory.CreateDirectory(directory);
            token.ThrowIfCancellationRequested();
            Move(source, destination, record["directory"]!.GetValue<bool>());
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { return XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
    }

    internal static IReadOnlyList<InstanceTrashedContent> Read(string instance, string game)
    {
        string root = Path.Combine(game, TrashFolder);
        RecoveryBlobStore.CheckLinks(root);
        if (!Directory.Exists(root)) return [];
        List<InstanceTrashedContent> entries = [];
        foreach (string directory in Directory.EnumerateDirectories(root).Take(1001))
        {
            if (entries.Count >= 1000) break;
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            try
            {
                var record = ReadRecord(directory, instance, game);
                string content = Path.Combine(directory, "content");
                RecoveryBlobStore.CheckLinks(content);
                if (Path.Exists(content)) entries.Add(new(Path.GetFileName(directory), record["page"]!.GetValue<string>(), record["name"]!.GetValue<string>(),
                    DateTimeOffset.Parse(record["removedAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or FormatException or System.Text.Json.JsonException) { }
        }
        return entries.OrderByDescending(item => item.RemovedAt).ToArray();
    }

    private static JsonObject ReadRecord(string directory, string instance, string game)
    {
        string path = Path.Combine(directory, "record.json");
        RecoveryBlobStore.CheckLinks(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > 16384) throw new InvalidDataException("回收记录超过限制。");
        byte[] bytes = new byte[16385];
        int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > 16384) throw new InvalidDataException("回收记录超过限制。");
        var record = JsonNode.Parse(bytes.AsSpan(0, count)) as JsonObject ?? throw new InvalidDataException("回收记录无效。");
        if (record["version"]?.GetValue<int>() != 1 || record["instance"]?.GetValue<string>() != instance || record["game"]?.GetValue<string>() != game
            || record["page"]?.GetValue<string>() is not { } page || !Pages.Contains(page)
            || record["name"]?.GetValue<string>() is not { } name || !MinecraftVersionPaths.IsSafeReference(name)
            || record["directory"] is not JsonValue || record["removedAt"] is not JsonValue)
            throw new InvalidDataException("回收记录不属于此实例。");
        return record;
    }

    private static void Move(string source, string destination, bool directory)
    { if (directory) Directory.Move(source, destination); else File.Move(source, destination, false); }

    internal static void RejectRunning(InstanceManagementSnapshot snapshot, XsrStateStore store, CancellationToken token)
    {
        if (store.TryResolve(MinecraftProcessStateComposition.SessionsKey, out var sessions)
            && store.ReadCollection<MinecraftProcessSnapshot>(sessions, cancellationToken: token).Items.Any(item => item.State is MinecraftProcessState.Created or MinecraftProcessState.Running
                && (Nexa.Core.PathIdentity.Comparer.Equals(item.InstanceDirectory, snapshot.InstanceDirectory)
                    || Nexa.Core.PathIdentity.Comparer.Equals(item.GameDirectory, snapshot.GameDirectory))))
            throw new InvalidOperationException("有游戏正在使用此目录，请先结束游戏进程。");
    }
}
