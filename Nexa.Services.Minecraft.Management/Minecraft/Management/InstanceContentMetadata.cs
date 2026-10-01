using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Nexa.Core.Media;
using Nexa.Services.Files;

namespace Nexa.Services.Minecraft.Management;

/// <summary>Untrusted presentation metadata only. Never executes content or resolves remote icons.</summary>
internal static class InstanceContentMetadata
{
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, CachedMetadata> Cache = new(Nexa.Core.PathIdentity.Comparer);
    private static readonly Queue<string> CacheOrder = new();
    private static long _cacheBytes;
    private sealed record CachedMetadata(string Page, long Length, long Modified, long ReadCost,
        long MemoryCost, InstanceContentEntry Entry);

    internal static async Task<InstanceContentSnapshot> EnrichAsync(InstanceContentSnapshot snapshot, string directory,
        ArchiveReadBudget budget, CancellationToken token)
    {
        List<InstanceContentEntry> entries = [];
        foreach (var entry in snapshot.Entries)
        {
            token.ThrowIfCancellationRequested();
            var result = entry with { DisplayName = entry.Name };
            if (budget.Remaining > 0 && (snapshot.PageId != "mods" || entry.Enabled is not null))
            {
                try
                {
                    string path = Path.GetFullPath(Path.Combine(directory, entry.Name));
                    CheckPath(path);
                    var info = new FileInfo(path);
                    bool cacheable = !entry.IsDirectory && snapshot.PageId is "mods" or "resourcepacks" or "shaderpacks";
                    long length = cacheable ? info.Length : 0, modified = cacheable ? info.LastWriteTimeUtc.Ticks : 0;
                    CachedMetadata? cached;
                    lock (CacheGate) Cache.TryGetValue(path, out cached);
                    if (cacheable && cached is not null && cached.Page == snapshot.PageId
                        && cached.Length == length && cached.Modified == modified && cached.ReadCost <= budget.Remaining)
                    {
                        budget.Consume(cached.ReadCost);
                        result = result with
                        {
                            DisplayName = cached.Entry.DisplayName,
                            Version = cached.Entry.Version,
                            Description = cached.Entry.Description,
                            Icon = cached.Entry.Icon,
                            PackageReadable = cached.Entry.PackageReadable,
                            PackageProblem = cached.Entry.PackageProblem
                        };
                    }
                    else
                    {
                        long before = budget.Remaining;
                        result = await ReadAsync(result, snapshot.PageId, path, budget, token).ConfigureAwait(false);
                        info.Refresh();
                        // Do not cache partial results caused by the shared read budget.
                        if (cacheable && before >= 4 * 1024 * 1024 && info.Exists
                            && info.Length == length && info.LastWriteTimeUtc.Ticks == modified)
                        {
                            long cost = 1024 + (result.Icon?.Bytes.Length ?? 0)
                                + 2L * ((result.DisplayName?.Length ?? 0) + (result.Description?.Length ?? 0));
                            lock (CacheGate)
                            {
                                if (Cache.Remove(path, out var old)) _cacheBytes -= old.MemoryCost;
                                else CacheOrder.Enqueue(path);
                                Cache[path] = new(snapshot.PageId, length, modified, before - budget.Remaining, cost, result);
                                _cacheBytes += cost;
                                while ((Cache.Count > 1024 || _cacheBytes > 64L * 1024 * 1024) && CacheOrder.TryDequeue(out var key))
                                    if (Cache.Remove(key, out var removed)) _cacheBytes -= removed.MemoryCost;
                            }
                        }
                    }
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
                {
                    if (snapshot.PageId == "mods" && entry.Enabled is not null)
                        result = result with { PackageProblem = "无法读取模组包或其元数据。" };
                }
            }
            entries.Add(result);
        }
        return snapshot with { Entries = entries.AsReadOnly() };
    }

    private static async Task<InstanceContentEntry> ReadAsync(InstanceContentEntry item, string page, string path,
        ArchiveReadBudget budget, CancellationToken token)
    {
        if (page == "screenshots")
        {
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return item;
            return item with { Icon = PngImage.TryCreatePreview(await ReadFile(path, 16 * 1024 * 1024, budget, token).ConfigureAwait(false)) };
        }
        if (page == "saves")
        {
            string icon = Path.Combine(path, "icon.png");
            return item.IsDirectory && File.Exists(icon)
                ? item with { Icon = PngImage.TryCreate(await ReadFile(icon, 1024 * 1024, budget, token).ConfigureAwait(false)) } : item;
        }
        if (page is not ("mods" or "resourcepacks" or "shaderpacks")) return item;
        ZipArchive? archive = null;
        FileStream? stream = null;
        Dictionary<string, ZipArchiveEntry?>? archiveIndex = null;
        try
        {
            if (!item.IsDirectory)
            {
                if (item.Size > 256L * 1024 * 1024) return item;
                CheckPath(path);
                stream = File.OpenRead(path);
                try
                {
                    archive = new ZipArchive(stream, ZipArchiveMode.Read);
                    if (archive.Entries.Count > 16384) return item;
                    archiveIndex = new(StringComparer.Ordinal);
                    foreach (var entry in archive.Entries)
                        if (!archiveIndex.TryAdd(entry.FullName, entry)) archiveIndex[entry.FullName] = null;
                }
                catch (InvalidDataException) when (page == "mods")
                { return item with { PackageReadable = false, PackageProblem = "模组包损坏或不是有效的 JAR 归档。" }; }
            }
            bool skippedMetadata = false, foundMetadata = false;
            async Task<byte[]?> Read(string name, int limit)
            {
                if (name.Length == 0 || name.Length > 512 || name.Contains('\\') || name.Contains(':')
                    || name.Split('/').Any(part => part is "" or "." or "..")) return null;
                if (archive is null)
                {
                    string file = Path.Combine(path, name.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(file)) return null;
                    if (new FileInfo(file).Length > Math.Min(limit, budget.Remaining)) { skippedMetadata = true; return null; }
                    return await ReadFile(file, limit, budget, token).ConfigureAwait(false);
                }
                if (!archiveIndex!.TryGetValue(name, out var match)) return null;
                if (match is null) throw new InvalidDataException("归档中存在重复的元数据条目。");
                if (match.Length > Math.Min(limit, budget.Remaining)) { skippedMetadata = true; return null; }
                using var input = match.Open(); using var output = new MemoryStream();
                await ArchiveReadBudget.CopyAsync(input, output, match.Length, limit, budget, token).ConfigureAwait(false);
                return output.ToArray();
            }
            string iconName = "pack.png", name = "", version = "", description = "";
            if (page == "mods")
            {
                foreach (string format in new[] { "fabric.mod.json", "quilt.mod.json", "META-INF/neoforge.mods.toml", "META-INF/mods.toml", "mcmod.info", "litemod.json" })
                {
                    byte[]? bytes = await Read(format, 256 * 1024).ConfigureAwait(false);
                    if (bytes is null) continue;
                    foundMetadata = true;
                    if (format.EndsWith(".toml", StringComparison.Ordinal))
                    {
                        string text = Encoding.UTF8.GetString(bytes);
                        string block = text.Split("[[mods]]", StringSplitOptions.None).Skip(1).FirstOrDefault() ?? text;
                        name = Literal(block, "displayName"); version = Literal(block, "version");
                        description = Literal(block, "description"); iconName = Literal(block, "logoFile");
                        if (version.Contains("${", StringComparison.Ordinal)) version = "";
                        if (version.Length == 0 && await Read("META-INF/MANIFEST.MF", 64 * 1024).ConfigureAwait(false) is { } manifest)
                            version = Encoding.UTF8.GetString(manifest).Split('\n').FirstOrDefault(line => line.StartsWith("Implementation-Version:", StringComparison.Ordinal))?[23..].Trim() ?? "";
                    }
                    else
                    {
                        JsonNode? root;
                        try { root = JsonNode.Parse(bytes); }
                        catch (System.Text.Json.JsonException) when (page == "mods")
                        { return item with { PackageReadable = false, PackageProblem = "模组元数据的 JSON 格式无效。" }; }
                        JsonNode? mod = root is JsonArray array ? array.FirstOrDefault() : root?["modList"] is JsonArray list ? list.FirstOrDefault() : root;
                        if (format == "quilt.mod.json") mod = root?["quilt_loader"];
                        JsonNode? display = mod?["metadata"] ?? mod;
                        name = Text(display?["name"]); version = Text(mod?["version"]);
                        description = Text(display?["description"]);
                        var icon = display?[format == "mcmod.info" ? "logoFile" : "icon"];
                        iconName = icon is JsonObject sizes ? Text(sizes["128"] ?? sizes["64"] ?? sizes.FirstOrDefault().Value) : Text(icon);
                    }
                    break;
                }
            }
            else if (await Read("pack.mcmeta", 256 * 1024).ConfigureAwait(false) is { } metadata)
            {
                var pack = JsonNode.Parse(metadata)?["pack"];
                description = Component(pack?["description"]);
                name = description.Split('\n')[0];
                version = pack?["pack_format"]?.ToJsonString() ?? "";
            }
            bool? readable = page == "mods" && foundMetadata && !skippedMetadata ? true : item.PackageReadable;
            PngImage? image = null;
            try
            {
                if (await Read(iconName, 1024 * 1024).ConfigureAwait(false) is { } png) image = PngImage.TryCreate(png);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            { /* Keep readable metadata when only the optional icon is invalid. */ }
            return item with { DisplayName = string.IsNullOrWhiteSpace(name) ? item.Name : Limit(name), Version = Limit(version), Description = Limit(description), Icon = image, PackageReadable = readable };
        }
        finally { archive?.Dispose(); stream?.Dispose(); }
    }
    private static string Limit(string text) => text.Length > 4096 ? text[..4096] : text;
    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static string Component(JsonNode? node, int depth = 0)
    {
        if (depth > 12) return "";
        if (node is JsonArray array) return Limit(string.Concat(array.Take(128).Select(item => Component(item, depth + 1))));
        if (node is JsonObject obj) return Limit(Text(obj["text"]) + Component(obj["extra"], depth + 1));
        return Text(node);
    }
    private static string Literal(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split('=', 2);
            if (parts.Length != 2 || parts[0].Trim() != key) continue;
            string value = parts[1].Trim();
            if (value.Length > 1 && value[0] is '\'' or '"' && value.LastIndexOf(value[0]) is var end && end > 0) return value[1..end];
        }
        return "";
    }
    private static void CheckPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能读取链接内容。");
    }
    private static async Task<byte[]> ReadFile(string path, int limit, ArchiveReadBudget budget, CancellationToken token)
    {
        CheckPath(path);
        using var input = File.OpenRead(path); using var output = new MemoryStream();
        await ArchiveReadBudget.CopyAsync(input, output, input.Length, limit, budget, token).ConfigureAwait(false);
        return output.ToArray();
    }
}
