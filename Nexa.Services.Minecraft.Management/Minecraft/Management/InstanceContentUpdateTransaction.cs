using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Management;

/// <summary>One durable compensation log for all downloaded content in an update.</summary>
public static partial class InstanceContentUpdateTransaction
{
    private const string Folder = ".nexa-content-updates";
    private const long MaxBytes = 2L * 1024 * 1024 * 1024;
    private static readonly string[] DigestFields = ["before", "after"];

    public static async Task ApplyAsync(IReadOnlyList<InstanceContentReplacement> replacements, XsrStateStore store, Func<CancellationToken, Task>? validate = null,
        CancellationToken token = default) => await ApplyCoreAsync(replacements, store, null, validate, token).ConfigureAwait(false);

    internal static async Task ApplyCoreAsync(IReadOnlyList<InstanceContentReplacement> replacements, XsrStateStore store,
        Action<int>? beforePublish, Func<CancellationToken, Task>? validate = null, CancellationToken token = default)
    {
        if (replacements.Count is 0 or > 100 || replacements.Any(item => item.Original.InstanceDirectory != replacements[0].Original.InstanceDirectory))
            throw new InvalidDataException("一次更新须属于同一实例，最多 100 项。");
        var snapshot = await InstanceManagementService.ReadAsync(new(replacements[0].Original.InstanceDirectory), token).ConfigureAwait(false);
        using var lease = await InstanceRecoveryOperationGate.EnterRestoreAsync(Directory.GetParent(snapshot.InstanceDirectory)!.Parent!.FullName, token).ConfigureAwait(false);
        snapshot = await InstanceManagementService.ReadAsync(new(snapshot.InstanceDirectory), token).ConfigureAwait(false);
        InstanceContentTrash.RejectRunning(snapshot, store, token);
        if (validate is not null) await validate(token).ConfigureAwait(false);
        var id = Guid.NewGuid();
        string directory = Path.Combine(snapshot.GameDirectory, Folder, id.ToString("N"));
        RecoveryBlobStore.CheckLinks(directory); Directory.CreateDirectory(directory);
        JsonArray entries = []; HashSet<string> targets = new(Nexa.Core.PathIdentity.Comparer); long bytes = 0;
        try
        {
            for (int index = 0; index < replacements.Count; index++)
            {
                var item = replacements[index]; var command = item.Original;
                string suffix = command.PageId == "mods" ? ".jar" : ".zip";
                string name = item.NewName;
                if (command.PageId == "mods" && command.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) name += ".disabled";
                if (command.PageId is not ("mods" or "resourcepacks" or "shaderpacks") || command.IsDirectory
                    || !MinecraftVersionPaths.IsSafeReference(command.Name) || !MinecraftVersionPaths.IsSafeReference(item.NewName)
                    || !item.NewName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    || !snapshot.Pages.Any(page => page.Id == command.PageId)) throw new InvalidDataException("更新内容类型无效。");
                string original = Path.Combine(snapshot.GameDirectory, command.PageId, command.Name), target = Path.Combine(snapshot.GameDirectory, command.PageId, name);
                RecoveryBlobStore.CheckLinks(original); RecoveryBlobStore.CheckLinks(target); RecoveryBlobStore.CheckLinks(item.StagedPath);
                var file = new FileInfo(original); var staged = new FileInfo(Path.GetFullPath(item.StagedPath));
                if (!file.Exists || file.Length != command.ExpectedSize || file.LastWriteTimeUtc.Ticks != command.ExpectedModifiedUtcTicks)
                    throw new IOException("内容已变化，请刷新后重新更新。");
                if (staged.Directory is not { } stage || !stage.Name.StartsWith(".nexa-resource-", StringComparison.Ordinal)
                    || !Nexa.Core.PathIdentity.Comparer.Equals(stage.Parent?.FullName, snapshot.GameDirectory) || !staged.Exists
                    || staged.Length is <= 0 or > 512L * 1024 * 1024) throw new InvalidDataException("更新暂存文件无效。");
                if (!targets.Add(original) || !Nexa.Core.PathIdentity.Comparer.Equals(original, target) && !targets.Add(target))
                    throw new InvalidDataException("更新列表含重复文件或目标冲突。");
                if (!Nexa.Core.PathIdentity.Comparer.Equals(original, target) && Path.Exists(target)) throw new IOException("目标文件已存在，未覆盖内容。");
                bytes = checked(bytes + file.Length + staged.Length);
                if (bytes > MaxBytes) throw new InvalidDataException("更新内容超过 2 GiB 事务预算。");
                string before = await HashAsync(original, token).ConfigureAwait(false), after = await HashAsync(staged.FullName, token).ConfigureAwait(false);
                file.Refresh();
                if (file.Length != command.ExpectedSize || file.LastWriteTimeUtc.Ticks != command.ExpectedModifiedUtcTicks) throw new IOException("准备更新期间内容变化。");
                File.Move(staged.FullName, Path.Combine(directory, index + ".new"), false);
                entries.Add((JsonNode)new JsonObject { ["page"] = command.PageId, ["original"] = command.Name, ["target"] = name, ["before"] = before, ["after"] = after });
            }
            var record = new JsonObject
            {
                ["version"] = 1,
                ["instance"] = snapshot.InstanceDirectory,
                ["game"] = snapshot.GameDirectory,
                ["created"] = DateTimeOffset.UtcNow.ToString("O"),
                ["phase"] = "applying",
                ["entries"] = entries
            };
            WriteRecord(directory, record);
            try
            {
                for (int index = 0; index < entries.Count; index++)
                {
                    token.ThrowIfCancellationRequested(); beforePublish?.Invoke(index);
                    var entry = entries[index]!;
                    string original = Original(snapshot, entry), target = Target(snapshot, entry);
                    if (await HashAsync(original, token).ConfigureAwait(false) != entry["before"]!.GetValue<string>()) throw new IOException("更新期间内容发生变化。");
                    InstanceContentTrash.RejectRunning(snapshot, store, token);
                    File.Move(original, Path.Combine(directory, index + ".old"), false);
                    File.Move(Path.Combine(directory, index + ".new"), target, false);
                }
                record["phase"] = "committed"; WriteRecord(directory, record);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            {
                try { await RollbackCoreAsync(snapshot, directory, record, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception rollback) when (rollback is not OutOfMemoryException and not AccessViolationException)
                { throw new AggregateException("更新未完成，联合撤回遇到冲突，已保留所有备份。", error, rollback); }
                throw;
            }
        }
        catch
        {
            // Without the durable plan no original was moved; only verified downloads are disposable.
            if (!File.Exists(Path.Combine(directory, "record.json")))
            {
                foreach (string path in Directory.EnumerateFiles(directory)) { RecoveryBlobStore.CheckLinks(path); File.Delete(path); }
                Directory.Delete(directory);
            }
            throw;
        }
    }

    public static async Task<XsrResult> RollbackAsync(InstanceContentUpdateRollbackCommand command, XsrStateStore store, CancellationToken token = default)
    {
        try
        {
            if (command.TransactionId == Guid.Empty) throw new InvalidDataException("更新记录无效。");
            var snapshot = await InstanceManagementService.ReadAsync(new(command.InstanceDirectory), token).ConfigureAwait(false);
            using var lease = await InstanceRecoveryOperationGate.EnterRestoreAsync(Directory.GetParent(snapshot.InstanceDirectory)!.Parent!.FullName, token).ConfigureAwait(false);
            InstanceContentTrash.RejectRunning(snapshot, store, token);
            string directory = Path.Combine(snapshot.GameDirectory, Folder, command.TransactionId.ToString("N"));
            await RollbackCoreAsync(snapshot, directory, ReadRecord(directory, snapshot), token).ConfigureAwait(false);
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
        { return XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
    }

    private static async Task RollbackCoreAsync(InstanceManagementSnapshot snapshot, string directory, JsonObject record, CancellationToken token)
    {
        if (record["phase"]!.GetValue<string>() == "rolled-back") return;
        var entries = (JsonArray)record["entries"]!;
        // Admit the entire reverse plan first so one conflicting item never silently undoes its peers.
        for (int index = 0; index < entries.Count; index++)
        {
            var item = entries[index]!; string original = Original(snapshot, item), target = Target(snapshot, item), backup = Path.Combine(directory, index + ".old");
            RecoveryBlobStore.CheckLinks(backup);
            if (!File.Exists(backup))
            {
                if (await HashAsync(original, token).ConfigureAwait(false) != item["before"]!.GetValue<string>()
                    || !Nexa.Core.PathIdentity.Comparer.Equals(original, target) && Path.Exists(target)) throw new IOException("更新原件缺失或已变化，已保留备份。");
                continue;
            }
            if (await HashAsync(backup, token).ConfigureAwait(false) != item["before"]!.GetValue<string>()
                || Path.Exists(target) && await HashAsync(target, token).ConfigureAwait(false) != item["after"]!.GetValue<string>()
                || !Nexa.Core.PathIdentity.Comparer.Equals(original, target) && Path.Exists(original))
                throw new IOException("更新后内容再次变化，已保留联合事务备份。");
        }
        token.ThrowIfCancellationRequested(); record["phase"] = "rolling-back"; WriteRecord(directory, record);
        for (int index = entries.Count - 1; index >= 0; index--)
        {
            var item = entries[index]!; string backup = Path.Combine(directory, index + ".old");
            if (!File.Exists(backup)) continue;
            string target = Target(snapshot, item); RecoveryBlobStore.CheckLinks(target);
            if (File.Exists(target)) File.Delete(target);
            string original = Original(snapshot, item); RecoveryBlobStore.CheckLinks(original); File.Move(backup, original, false);
        }
        record["phase"] = "rolled-back"; WriteRecord(directory, record);
    }

    internal static IReadOnlyList<InstanceContentUpdateRecord> Read(InstanceManagementSnapshot snapshot)
    {
        string root = Path.Combine(snapshot.GameDirectory, Folder); RecoveryBlobStore.CheckLinks(root);
        if (!Directory.Exists(root)) return [];
        List<InstanceContentUpdateRecord> entries = [];
        foreach (string directory in Directory.EnumerateDirectories(root).Take(1000))
            try
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) continue;
                var record = ReadRecord(directory, snapshot);
                entries.Add(new(id, DateTimeOffset.Parse(record["created"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture), ((JsonArray)record["entries"]!).Count, record["phase"]!.GetValue<string>()));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        return entries.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    private static JsonObject ReadRecord(string directory, InstanceManagementSnapshot snapshot)
    {
        string path = Path.Combine(directory, "record.json"); RecoveryBlobStore.CheckLinks(path);
        using var input = File.OpenRead(path);
        if (input.Length > 1024 * 1024) throw new InvalidDataException("更新记录过大。");
        byte[] bytes = new byte[1024 * 1024 + 1]; int count = input.ReadAtLeast(bytes, bytes.Length, false);
        if (count > 1024 * 1024) throw new InvalidDataException("更新记录过大。");
        var record = JsonNode.Parse(bytes.AsSpan(0, count)) as JsonObject ?? throw new InvalidDataException("更新记录无效。");
        return ValidateRecord(record, snapshot);
    }

    private static JsonObject ValidateRecord(JsonObject record, InstanceManagementSnapshot snapshot)
    {
        if (record["version"]?.GetValue<int>() != 1 || record["instance"]?.GetValue<string>() != snapshot.InstanceDirectory
            || record["game"]?.GetValue<string>() != snapshot.GameDirectory || record["phase"]?.GetValue<string>() is not ("applying" or "committed" or "rolling-back" or "rolled-back")
            || record["entries"] is not JsonArray entries || entries.Count is 0 or > 100) throw new InvalidDataException("更新记录不属于此实例。");
        HashSet<string> paths = new(Nexa.Core.PathIdentity.Comparer);
        foreach (var entry in entries)
        {
            if (entry is not JsonObject || entry["page"]?.GetValue<string>() is not ("mods" or "resourcepacks" or "shaderpacks")
                || !MinecraftVersionPaths.IsSafeReference(entry["original"]?.GetValue<string>()) || !MinecraftVersionPaths.IsSafeReference(entry["target"]?.GetValue<string>())
                || DigestFields.Any(key => entry[key]?.GetValue<string>() is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit)))
                throw new InvalidDataException("更新记录范围无效。");
            string original = Original(snapshot, entry), target = Target(snapshot, entry);
            if (!paths.Add(original) || !Nexa.Core.PathIdentity.Comparer.Equals(original, target) && !paths.Add(target)) throw new InvalidDataException("更新记录范围重复。");
        }
        return record;
    }

    private static string Original(InstanceManagementSnapshot snapshot, JsonNode entry) => Path.Combine(snapshot.GameDirectory, entry["page"]!.GetValue<string>(), entry["original"]!.GetValue<string>());
    private static string Target(InstanceManagementSnapshot snapshot, JsonNode entry) => Path.Combine(snapshot.GameDirectory, entry["page"]!.GetValue<string>(), entry["target"]!.GetValue<string>());
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (input.Length > MaxBytes) throw new InvalidDataException("更新文件过大。");
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
    }
    private static void WriteRecord(string directory, JsonObject record)
    {
        string temporary = Path.Combine(directory, "record.part"), target = Path.Combine(directory, "record.json");
        RecoveryBlobStore.CheckLinks(temporary); RecoveryBlobStore.CheckLinks(target);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(record.ToJsonString());
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { output.Write(bytes); output.Flush(true); }
        File.Move(temporary, target, true);
    }
}
