using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using static Nexa.Services.Resources.ResourceProviderHttp;

namespace Nexa.Services.Resources;

public sealed class ResourceInstanceService(ResourceProviderHttp http, ResourceOnlineInformationCache? information = null) : IDisposable
{
    public ResourceInstanceService(ResourceProviderHttp http) : this(http, null) { }
    internal sealed record Fingerprint(string Name, long Size, long Modified, string Sha512, string Sha1, uint CurseForge, bool Enabled);
    private readonly ResourceOnlineInformationCache _information = information ?? new();
    public void Dispose() { if (information is null) _information.Dispose(); }
    public Task<ResourceInstanceContext> ReadAsync(ResourceInstanceQuery query, CancellationToken token) => Task.Run(async () =>
    {
        var edit = await MinecraftInstallEditService.ReadAsync(new(query.Root, query.InstanceId), token).ConfigureAwait(false);
        string instance = Path.Combine(Path.GetFullPath(query.Root), "versions", query.InstanceId);
        CheckLinks(instance);
        var metadata = await new MinecraftInstanceMetadataStore().LoadAsync(instance, token).ConfigureAwait(false);
        string mods = Path.Combine(metadata.InstanceIsolation ? instance : Path.GetFullPath(query.Root), "mods");
        string loader = edit.Selection.Select(s => s.Loader switch { InstallLoader.Fabric => "fabric", InstallLoader.LegacyFabric => "fabric", InstallLoader.Quilt => "quilt", InstallLoader.Forge or InstallLoader.Cleanroom => "forge", InstallLoader.NeoForge => "neoforge", _ => "" }).FirstOrDefault(s => s.Length > 0) ?? "";
        if (!Directory.Exists(mods))
        {
            token.ThrowIfCancellationRequested();
            return new ResourceInstanceContext(edit.GameVersion, loader, [], null) { GameDirectory = Path.GetDirectoryName(mods)! };
        }
        CheckLinks(mods);
        List<Fingerprint> files = [];
        long budget = 2L * 1024 * 1024 * 1024;
        bool complete = true;
        foreach (string path in Directory.EnumerateFiles(mods).Where(p => p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || (p.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".disabled.jar", StringComparison.OrdinalIgnoreCase))).Take(513))
        {
            token.ThrowIfCancellationRequested();
            if (files.Count >= 512) { complete = false; break; }
            var file = new FileInfo(path);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > 256L * 1024 * 1024 || file.Length > budget) { complete = false; continue; }
            budget -= file.Length;
            var fingerprint = await ReadFingerprintAsync(file, token).ConfigureAwait(false);
            if (fingerprint is not null) files.Add(fingerprint); else complete = false;
        }
        var lookups = await IdentifyManyAsync(files, query.MirrorFirst, token).ConfigureAwait(false);
        var installed = files.SelectMany(file => lookups.TryGetValue(file.Sha512, out var value)
            ? value.Files.Select(match => match with { FileName = file.Name, Enabled = file.Enabled }) : []).DistinctBy(f => (f.Source, f.FileName)).ToArray();
        token.ThrowIfCancellationRequested();
        return new ResourceInstanceContext(edit.GameVersion, loader, installed,
            lookups.Values.Any(value => value.IsStale) ? ResourceOnlineInformationCache.StaleNotice
            : complete && lookups.Values.All(r => r.Complete) ? null : "部分模组暂时无法识别，未识别的项目仍会显示。")
        { GameDirectory = Path.GetDirectoryName(mods)! };
    }, token);
    internal async Task<Fingerprint?> ReadFingerprintAsync(FileInfo file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long size = file.Length, modified = file.LastWriteTimeUtc.Ticks;
        string identity = ResourceOnlineInformationCache.Identity(file.FullName, size.ToString(System.Globalization.CultureInfo.InvariantCulture), modified.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var fingerprint = await _information.ReadAsync("fingerprint", identity, ResourceOnlineInformationCache.MetadataFresh, ResourceOnlineInformationCache.MetadataRetain,
            1024, ResourceMetadataJsonContext.Default.ResourceMetadataEnvelopeFingerprintMetadata,
            async ct => new FingerprintMetadata(await FingerprintAsync(file, ct).ConfigureAwait(false)),
            value => value.Fingerprint is { } f && f.Size == size && f.Modified == modified && f.Sha512.Length == 128 && f.Sha512.All(char.IsAsciiHexDigit)
                && f.Sha1.Length == 40 && f.Sha1.All(char.IsAsciiHexDigit), value => value, value => value, token: token).ConfigureAwait(false);
        file.Refresh();
        return file.Exists && file.Length == size && file.LastWriteTimeUtc.Ticks == modified
            ? fingerprint.Fingerprint is { } value ? value with { Name = file.Name, Enabled = Enabled(file.Name) } : null : null;
    }
    internal async Task<ResourceIdentityMetadata> IdentifyAsync(Fingerprint file, bool mirror, CancellationToken token, bool refresh = false, bool waitForRefresh = false)
    {
        var result = await IdentifyManyAsync([file], mirror, token, refresh, waitForRefresh).ConfigureAwait(false);
        var value = result[file.Sha512];
        return value with { Files = value.Files.Select(match => match with { FileName = file.Name, Enabled = file.Enabled }).ToArray() };
    }
    internal async Task<IReadOnlyDictionary<string, ResourceIdentityMetadata>> IdentifyManyAsync(List<Fingerprint> files, bool mirror, CancellationToken token, bool refresh = false, bool waitForRefresh = false)
    {
        token.ThrowIfCancellationRequested();
        string policy = ResourceOnlineInformationCache.Identity(http.CachePolicyIdentity, mirror.ToString());
        var identified = new Dictionary<string, ResourceIdentityMetadata>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<Fingerprint>();
        foreach (var file in files.DistinctBy(file => file.Sha512, StringComparer.OrdinalIgnoreCase))
        {
            var cached = await _information.ReadSnapshotAsync("identity", ResourceOnlineInformationCache.Identity(policy, file.Sha512),
                ResourceOnlineInformationCache.IdentityFresh, ResourceOnlineInformationCache.MetadataRetain, 2048,
                ResourceMetadataJsonContext.Default.ResourceMetadataEnvelopeResourceIdentityMetadata,
                value => Verified(value, file.Sha512), token).ConfigureAwait(false);
            if (cached is not null)
            {
                identified[file.Sha512] = cached.Value with { IsStale = refresh || cached.IsStale, Complete = !refresh && !cached.IsStale && cached.Value.Complete };
                if (refresh || cached.IsStale) missing.Add(file);
            }
            else missing.Add(file);
        }
        if (missing.Count == 0) return identified;
        string batchIdentity = ResourceOnlineInformationCache.Identity(policy, string.Join(",", files.Select(file => file.Sha512).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)));
        async Task<ResourceIdentityBatchMetadata> Read(CancellationToken ct)
        {
            var results = await Task.WhenAll(ReadModrinth(missing, mirror, ct), ReadCurseForge(missing, mirror, ct)).ConfigureAwait(false);
            bool complete = results.All(result => result.Complete);
            var matches = results.SelectMany(result => result.Files).ToLookup(match => match.Sha512, StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, ResourceIdentityMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in missing)
            {
                var value = new ResourceIdentityMetadata(matches[file.Sha512].DistinctBy(match => (match.Source, match.VersionId)).ToArray(), complete);
                values[file.Sha512] = value;
                if (Verified(value, file.Sha512))
                    await _information.SaveAsync("identity", ResourceOnlineInformationCache.Identity(policy, file.Sha512), value,
                        ResourceOnlineInformationCache.IdentityFresh, ResourceOnlineInformationCache.MetadataRetain, 2048,
                        ResourceMetadataJsonContext.Default.ResourceMetadataEnvelopeResourceIdentityMetadata,
                        value => value with { Files = value.Files.Select(match => match with { FileName = "", Enabled = false }).ToArray() }, ct).ConfigureAwait(false);
            }
            return new(values);
        }
        async Task<ResourceIdentityBatchMetadata> Fetch(CancellationToken waiter) => await _information.ReadAsync("identity-batch", batchIdentity,
            ResourceOnlineInformationCache.IdentityFresh, ResourceOnlineInformationCache.MetadataRetain, Math.Max(2048, missing.Count * 2048),
            ResourceMetadataJsonContext.Default.ResourceMetadataEnvelopeResourceIdentityBatchMetadata, Read,
            value => value.Matches.Values.All(item => item.Complete && item.Files.Length > 0), value => value,
            value => new(value.Matches.ToDictionary(item => item.Key, item => item.Value with { IsStale = true, Complete = false }, StringComparer.OrdinalIgnoreCase)),
            refresh: refresh, persistent: false, waitForRefresh: waitForRefresh, token: waiter).ConfigureAwait(false);
        if (!refresh && !waitForRefresh && missing.All(file => identified.ContainsKey(file.Sha512)))
        {
            _ = Refresh();
            return identified;
        }
        var current = await Fetch(token).ConfigureAwait(false);
        foreach (var item in current.Matches)
            if (item.Value.Files.Length > 0 || !identified.ContainsKey(item.Key)) identified[item.Key] = item.Value;
        return identified;
        async Task Refresh() { try { await Fetch(CancellationToken.None).ConfigureAwait(false); } catch (Exception error) when (ResourceOnlineInformationCache.Recoverable(error)) { } }
    }
    private static bool Verified(ResourceIdentityMetadata value, string hash) => value.Complete && value.Files.Length is > 0 and <= 2
        && value.Files.All(match => Enum.IsDefined(match.Source.Provider) && match.Sha512.Equals(hash, StringComparison.OrdinalIgnoreCase)
            && (match.Source.Provider == ResourceProvider.Modrinth ? ModrinthId(match.Source.ProjectId) && ModrinthId(match.VersionId)
                : CurseForgeId(match.Source.ProjectId) && CurseForgeId(match.VersionId)));
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
                    if (version.TryGetProperty("files", out var actualFiles) && (actualFiles.ValueKind != JsonValueKind.Array
                        || !actualFiles.EnumerateArray().Any(actual => actual.ValueKind == JsonValueKind.Object && actual.TryGetProperty("hashes", out var hashes)
                            && Text(hashes, "sha512").Equals(file.Sha512, StringComparison.OrdinalIgnoreCase)))) { complete = false; continue; }
                    matches.Add(new(new(ResourceProvider.Modrinth, project), id, file.Name, file.Sha512, file.Enabled));
                }
            }
            token.ThrowIfCancellationRequested(); return (matches.ToArray(), complete);
        }
        catch (Exception e) when (!token.IsCancellationRequested && ResourceOnlineInformationCache.Recoverable(e)) { return (matches.ToArray(), false); }
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
                    foreach (var file in batch.Where(f => f.CurseForge == (Number(version, "fileFingerprint") is > 0 and <= uint.MaxValue ? Number(version, "fileFingerprint") : Number(match, "id")) && f.Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase)))
                        matches.Add(new(new(ResourceProvider.CurseForge, project), id, file.Name, file.Sha512, file.Enabled));
                }
            }
            token.ThrowIfCancellationRequested(); return (matches.ToArray(), complete);
        }
        catch (Exception e) when (!token.IsCancellationRequested && ResourceOnlineInformationCache.Recoverable(e)) { return (matches.ToArray(), false); }
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
        return actual == size && file.Exists && file.Length == size && file.LastWriteTimeUtc.Ticks == modified ? new(file.Name, size, modified, Convert.ToHexString(sha512.GetHashAndReset()).ToLowerInvariant(), Convert.ToHexString(sha1.GetHashAndReset()), hash, Enabled(file.Name)) : null;
    }
    private static bool Enabled(string name) => !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".disabled.jar", StringComparison.OrdinalIgnoreCase);
    private static bool Whitespace(byte b) => b is 9 or 10 or 13 or 32;
    private static void CheckLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能读取链接目录中的模组。");
    }
}
