using System.Security.Cryptography;
using Nexa.Services.Files;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceModRemovalQuery(InstanceContentRemoveCommand Primary);
public sealed record InstanceModRemovalPreview(InstanceContentRemoveCommand Primary, IReadOnlyList<InstanceContentRemoveCommand> Orphans, IReadOnlyList<string> RequiredBy, string? Notice);
public sealed record InstanceModRemovalCommand(InstanceContentRemoveCommand Primary, IReadOnlyList<InstanceContentRemoveCommand> Orphans);

public static class InstanceModRemovalService
{
    public static Task<InstanceModRemovalPreview> PreviewAsync(InstanceModRemovalQuery query, CancellationToken token = default) => Task.Run(async () =>
    {
        var command = query.Primary;
        if (command.PageId != "mods" || command.IsDirectory || !MinecraftVersionPaths.IsSafeReference(command.Name)) throw new InvalidDataException("请选择模组文件。");
        var snapshot = await InstanceManagementService.ReadAsync(new(command.InstanceDirectory), token).ConfigureAwait(false);
        var content = snapshot.Contents.FirstOrDefault(page => page.PageId == "mods");
        var target = content?.Entries.FirstOrDefault(item => item.Name == command.Name);
        if (target is null || target.Size != command.ExpectedSize || target.ModifiedUtcTicks != command.ExpectedModifiedUtcTicks) throw new IOException("模组已变化，请刷新后重试。");
        var inventory = await LaunchModInventoryReader.ReadAsync(snapshot.GameDirectory, token).ConfigureAwait(false);
        if (!inventory.Complete || inventory.Mods.Any(mod => !mod.DependenciesComplete || mod.NestedCandidate) || content?.Complete != true)
            return new InstanceModRemovalPreview(command, [], [], "部分依赖尚不能完整确认，仅移除此模组。");
        string directory = snapshot.Pages.Single(page => page.Id == "mods").Directory!;
        var budget = new ArchiveReadBudget(2L * 1024 * 1024 * 1024);
        var identities = new Dictionary<string, List<InstanceContentEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in content.Entries.Where(item => item.Enabled is not null).Take(513))
        {
            if (identities.Values.Sum(items => items.Count) >= 512) return new InstanceModRemovalPreview(command, [], [], "模组较多，暂不建议一并移除前置。");
            string path = Path.Combine(directory, entry.Name); RecoveryBlobStore.CheckLinks(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var hash = SHA256.Create(); await using var output = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write, true);
            await ArchiveReadBudget.CopyAsync(input, output, entry.Size!.Value, 256L * 1024 * 1024, budget, token).ConfigureAwait(false);
            await output.FlushFinalBlockAsync(token).ConfigureAwait(false);
            var info = new FileInfo(path);
            if (info.Length != entry.Size || info.LastWriteTimeUtc.Ticks != entry.ModifiedUtcTicks) throw new IOException("模组已变化，请刷新后重试。");
            string digest = Convert.ToHexString(hash.Hash!);
            if (!identities.TryGetValue(digest, out var items)) identities[digest] = items = [];
            items.Add(entry);
        }
        if (identities.Values.Any(items => items.Count != 1)) return new InstanceModRemovalPreview(command, [], [], "存在内容相同的模组文件，仅移除此模组。");
        var nodes = inventory.Mods.Where(mod => mod.ContentSha256 is { } hash && identities.ContainsKey(hash))
            .Select(mod => (Mod: mod, File: identities[mod.ContentSha256!][0])).ToArray();
        var main = nodes.Where(node => node.File.Name == command.Name).ToArray();
        if (main.Length == 0) return new InstanceModRemovalPreview(command, [], [], "未识别此模组的依赖，仅移除此文件。");
        var removing = new HashSet<string>(StringComparer.Ordinal) { command.Name };
        var pending = new Queue<string>(main.Where(node => node.Mod.Enabled).SelectMany(node => node.Mod.Dependencies.Keys));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out string? id))
        {
            if (!visited.Add(id)) continue;
            foreach (var node in nodes.Where(node => node.Mod.Enabled && (node.Mod.Id == id || node.Mod.ProvidedIds.ContainsKey(id))))
                if (removing.Add(node.File.Name))
                    foreach (var bundled in nodes.Where(item => item.File.Name == node.File.Name))
                        foreach (string dependency in bundled.Mod.Dependencies.Keys) pending.Enqueue(dependency);
        }
        bool changed;
        do
        {
            changed = false;
            foreach (string candidate in removing.Where(name => name != command.Name).ToArray())
            {
                var ids = nodes.Where(node => node.File.Name == candidate).SelectMany(node => node.Mod.ProvidedIds.Keys.Prepend(node.Mod.Id)).ToHashSet(StringComparer.Ordinal);
                if (nodes.Any(node => node.Mod.Enabled && !removing.Contains(node.File.Name) && node.Mod.Dependencies.Keys.Any(ids.Contains)))
                { removing.Remove(candidate); changed = true; }
            }
        } while (changed);
        var primaryIds = main.SelectMany(node => node.Mod.ProvidedIds.Keys.Prepend(node.Mod.Id)).ToHashSet(StringComparer.Ordinal);
        string[] requiredBy = nodes.Where(node => node.Mod.Enabled && !removing.Contains(node.File.Name) && node.Mod.Dependencies.Keys.Any(primaryIds.Contains))
            .Select(node => node.File.DisplayName.Length > 0 ? node.File.DisplayName : node.File.Name).Distinct().ToArray();
        var orphans = content.Entries.Where(entry => removing.Contains(entry.Name) && entry.Name != command.Name)
            .Select(entry => new InstanceContentRemoveCommand(snapshot.InstanceDirectory, "mods", entry.Name, false, entry.Size, entry.ModifiedUtcTicks)).ToArray();
        return new InstanceModRemovalPreview(command, orphans, requiredBy, null);
    }, token);

    public static async Task<XsrResult> RemoveAsync(InstanceModRemovalCommand command, XsrStateStore store, CancellationToken token = default)
    {
        try
        {
            if (command.Orphans.Count > 512 || command.Orphans.Distinct().Count() != command.Orphans.Count) throw new InvalidDataException("移除列表无效。");
            return await InstanceContentTrash.RemoveBatchAsync([command.Primary, .. command.Orphans], store, async currentToken =>
            {
                var preview = await PreviewAsync(new(command.Primary), currentToken).ConfigureAwait(false);
                if (command.Orphans.Any(item => !preview.Orphans.Contains(item))) throw new IOException("依赖关系已变化，请重新确认移除列表。");
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        { return XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
    }
}
