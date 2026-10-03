using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using static Nexa.Services.Resources.ResourceProviderHttp;

namespace Nexa.Services.Resources;

public sealed class ResourceInstanceService(ResourceProviderHttp http)
{
    internal sealed record Fingerprint(string Name, long Size, long Modified, string Sha512, string Sha1, uint CurseForge, bool Enabled);
    private readonly ResourceSnapshotCache<string, Fingerprint> _cache = new(512);
    private readonly ResourceSnapshotCache<string, (ResourceInstalledFile[] Files, bool Complete)> _identified = new(1024);
    public Task<ResourceInstanceContext> ReadAsync(ResourceInstanceQuery query, CancellationToken token) => Task.Run(async () =>
    {
        var edit = await MinecraftInstallEditService.ReadAsync(new(query.Root, query.InstanceId), token).ConfigureAwait(false);
        string instance = Path.Combine(Path.GetFullPath(query.Root), "versions", query.InstanceId);
        CheckLinks(instance);
        var metadata = await new MinecraftInstanceMetadataStore().LoadAsync(instance, token).ConfigureAwait(false);
        string mods = Path.Combine(metadata.InstanceIsolation ? instance : Path.GetFullPath(query.Root), "mods");
        string loader = edit.Selection.Select(s => s.Loader switch { InstallLoader.Fabric => "fabric", InstallLoader.LegacyFabric => "fabric", InstallLoader.Quilt => "quilt", InstallLoader.Forge or InstallLoader.Cleanroom => "forge", InstallLoader.NeoForge => "neoforge", _ => "" }).FirstOrDefault(s => s.Length > 0) ?? "";
        if (!Directory.Exists(mods)) return new ResourceInstanceContext(edit.GameVersion, loader, [], null) { GameDirectory = Path.GetDirectoryName(mods)! };
        CheckLinks(mods);
        List<Fingerprint> files = [];
        long budget = 2L * 1024 * 1024 * 1024;
        bool complete = true;
        foreach (string path in Directory.EnumerateFiles(mods).Where(p => p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)).Take(513))
        {
            token.ThrowIfCancellationRequested();
            if (files.Count >= 512) { complete = false; break; }
            var file = new FileInfo(path);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > 256L * 1024 * 1024 || file.Length > budget) { complete = false; continue; }
            budget -= file.Length;
            var fingerprint = await ReadFingerprintAsync(file, token).ConfigureAwait(false);
            if (fingerprint is not null) files.Add(fingerprint); else complete = false;
        }
        var lookups = await Task.WhenAll(ReadModrinth(files, query.MirrorFirst, token), ReadCurseForge(files, query.MirrorFirst, token)).ConfigureAwait(false);
        return new ResourceInstanceContext(edit.GameVersion, loader, lookups.SelectMany(r => r.Files).DistinctBy(f => (f.Source, f.FileName)).ToArray(),
            complete && lookups.All(r => r.Complete) ? null : "部分模组暂时无法识别，未识别的项目仍会显示。")
        { GameDirectory = Path.GetDirectoryName(mods)! };
    }, token);
    internal async Task<Fingerprint?> ReadFingerprintAsync(FileInfo file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string key = file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        if (_cache.TryRead(key, out var cached)) return cached;
        var fingerprint = await FingerprintAsync(file, token).ConfigureAwait(false);
        if (fingerprint is not null) _cache.Save(key, fingerprint);
        return fingerprint;
    }
    internal Task<(ResourceInstalledFile[] Files, bool Complete)> IdentifyAsync(Fingerprint file, bool mirror, CancellationToken token) => IdentifyAsync(file, mirror, false, token);
    internal async Task<(ResourceInstalledFile[] Files, bool Complete)> IdentifyAsync(Fingerprint file, bool mirror, bool useCache, CancellationToken token)
    {
        string key = file.Sha512 + mirror;
        if (useCache && _identified.TryRead(key, out var cached))
            return (cached.Files.Select(f => f with { FileName = file.Name, Enabled = file.Enabled }).ToArray(), cached.Complete);
        var results = await Task.WhenAll(ReadModrinth([file], mirror, token), ReadCurseForge([file], mirror, token)).ConfigureAwait(false);
        var result = (Files: results.SelectMany(result => result.Files).ToArray(), Complete: results.All(result => result.Complete));
        if (result.Complete) _identified.Save(key, result);
        return result;
    }
    internal async Task IdentifyManyAsync(List<Fingerprint> files, bool mirror, CancellationToken token)
    {
        var missing = files.Where(f => !_identified.TryRead(f.Sha512 + mirror, out _)).ToList();
        if (missing.Count == 0) return;
        var results = await Task.WhenAll(ReadModrinth(missing, mirror, token), ReadCurseForge(missing, mirror, token)).ConfigureAwait(false);
        bool complete = results.All(r => r.Complete);
        if (!complete) return;
        var matches = results.SelectMany(r => r.Files).ToLookup(f => f.Sha512, StringComparer.OrdinalIgnoreCase);
        foreach (var file in missing) _identified.Save(file.Sha512 + mirror, (matches[file.Sha512].ToArray(), true));
    }
    private async Task<(ResourceInstalledFile[] Files, bool Complete)> ReadModrinth(List<Fingerprint> files, bool mirror, CancellationToken token)
    {
        List<ResourceInstalledFile> matches = [];
        bool complete = true;
        try
        {
            foreach (var batch in files.Chunk(100))
            {
                var json = new JsonObject { ["hashes"] = new JsonArray(batch.Select(f => (JsonNode?)JsonValue.Create(f.Sha512)).ToArray()), ["algorithm"] = "sha512" };
                using var document = await http.PostAsync(ResourceProvider.Modrinth, "version_files", mirror, json.ToJsonString(), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("资源站返回了无效的文件识别结果。");
                foreach (var file in batch)
                {
                    if (!document.RootElement.TryGetProperty(file.Sha512, out var version)) continue;
                    string project = Text(version, "project_id"), id = Text(version, "id");
                    if (version.ValueKind != JsonValueKind.Object || !ModrinthId(project) || !ModrinthId(id)) { complete = false; continue; }
                    matches.Add(new(new(ResourceProvider.Modrinth, project), id, file.Name, file.Sha512, file.Enabled));
                }
            }
            token.ThrowIfCancellationRequested(); return (matches.ToArray(), complete);
        }
        catch (Exception e) when (!token.IsCancellationRequested && e is IOException or InvalidDataException or HttpRequestException or JsonException or OperationCanceledException) { return (matches.ToArray(), false); }
    }
    private async Task<(ResourceInstalledFile[] Files, bool Complete)> ReadCurseForge(List<Fingerprint> files, bool mirror, CancellationToken token)
    {
        List<ResourceInstalledFile> matches = [];
        bool complete = true;
        try
        {
            foreach (var batch in files.Chunk(100))
            {
                var json = new JsonObject { ["fingerprints"] = new JsonArray(batch.Select(f => (JsonNode?)JsonValue.Create(f.CurseForge)).ToArray()) };
                using var document = await http.PostAsync(ResourceProvider.CurseForge, "fingerprints/432", mirror, json.ToJsonString(), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("exactMatches", out var exact) || exact.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("资源站返回了无效的文件识别结果。");
                foreach (var match in exact.EnumerateArray())
                {
                    if (match.ValueKind != JsonValueKind.Object || !match.TryGetProperty("file", out var version) || version.ValueKind != JsonValueKind.Object
                        || !version.TryGetProperty("hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Array) { complete = false; continue; }
                    string project = Text(version, "modId"), id = Text(version, "id");
                    if (!CurseForgeId(project) || !CurseForgeId(id)) { complete = false; continue; }
                    string sha1 = hashes.EnumerateArray().Where(h => Number(h, "algo") == 1).Select(h => Text(h, "value")).FirstOrDefault() ?? "";
                    if (sha1.Length != 40 || !sha1.All(char.IsAsciiHexDigit)) { complete = false; continue; }
                    foreach (var file in batch.Where(f => f.CurseForge == Number(version, "fileFingerprint") && f.Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase)))
                        matches.Add(new(new(ResourceProvider.CurseForge, project), id, file.Name, file.Sha512, file.Enabled));
                }
            }
            token.ThrowIfCancellationRequested(); return (matches.ToArray(), complete);
        }
        catch (Exception e) when (!token.IsCancellationRequested && e is IOException or InvalidDataException or HttpRequestException or JsonException or OperationCanceledException) { return (matches.ToArray(), false); }
    }
    private static bool ModrinthId(string value) => value.Length is > 0 and <= 64 && value.All(char.IsAsciiLetterOrDigit);
    private static bool CurseForgeId(string value) => value.Length is > 0 and < 20 && value.All(char.IsAsciiDigit);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "CurseForge file identity verification uses its published SHA1 in addition to the Murmur2 fingerprint; not a credential or signature.")]
    private static async Task<Fingerprint?> FingerprintAsync(FileInfo file, CancellationToken token)
    {
        long size = file.Length, modified = file.LastWriteTimeUtc.Ticks;
        await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] buffer = new byte[65536]; long actual = 0; uint normalized = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            actual += read; if (actual > size) throw new IOException("模组文件已变化。");
            sha512.AppendData(buffer, 0, read); sha1.AppendData(buffer, 0, read);
            for (int i = 0; i < read; i++) if (!Whitespace(buffer[i])) normalized++;
        }
        if (actual != size) return null;
        input.Position = 0;
        actual = 0;
        uint hash = 1 ^ normalized, word = 0; int count = 0;
        unchecked
        {
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                actual += read; if (actual > size) throw new IOException("资源文件已变化。");
                for (int i = 0; i < read; i++)
                {
                    byte b = buffer[i]; if (Whitespace(b)) continue;
                    word |= (uint)b << (count * 8);
                    if (++count == 4) { word *= 0x5bd1e995; word ^= word >> 24; word *= 0x5bd1e995; hash = hash * 0x5bd1e995 ^ word; word = 0; count = 0; }
                }
            }
            if (count > 0) { hash ^= word; hash *= 0x5bd1e995; }
            hash ^= hash >> 13; hash *= 0x5bd1e995; hash ^= hash >> 15;
        }
        file.Refresh();
        return actual == size && file.Exists && file.Length == size && file.LastWriteTimeUtc.Ticks == modified ? new(file.Name, size, modified, Convert.ToHexString(sha512.GetHashAndReset()).ToLowerInvariant(), Convert.ToHexString(sha1.GetHashAndReset()), hash, !file.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) : null;
    }
    private static bool Whitespace(byte b) => b is 9 or 10 or 13 or 32;
    private static void CheckLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能读取链接目录中的模组。");
    }
}
