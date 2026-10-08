using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Assets;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Libraries;
using Nexa.Services.Minecraft.ModLoaders;

namespace Nexa.Services.Minecraft.Management;

/// <summary>Local, bounded prerequisites only; never downloads or relaxes launch admission.</summary>
public sealed partial class InstanceOfflineReadinessService(
    Func<string, MinecraftJavaRequirementRequest, MinecraftInstanceMetadata, CancellationToken, Task<JavaSelectionResult>>? selectJava = null,
    MinecraftLaunchPlatform? platform = null)
{
    public const int MaximumArtifacts = 65536;
    public const long MaximumHashBytes = 8L * 1024 * 1024 * 1024;
    private sealed record Manifest(string Path, JsonObject Json);
    private sealed record Expected(string Category, string Path, long? Size, string? Sha1, string? Sha256 = null);
    private sealed class Budget
    {
        internal long JsonBytes, HashBytes;
        internal readonly Dictionary<string, (long Bytes, long Modified)> Stamps = new(Nexa.Core.PathIdentity.Comparer);
    }

    public Task<InstanceOfflineReadinessReport> ReadAsync(InstanceOfflineReadinessQuery query, CancellationToken token = default) => Task.Run(async () =>
    {
        if (!Path.IsPathFullyQualified(query.InstanceDirectory)) throw new IOException("请选择有效的版本目录。");
        string instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(query.InstanceDirectory));
        var versions = Directory.GetParent(instance);
        if (versions?.Name != "versions" || versions.Parent is null || !MinecraftVersionPaths.IsSafeReference(Path.GetFileName(instance))) throw new IOException("版本必须位于游戏目录的 versions 下。");
        string root = versions.Parent.FullName; CheckLinks(instance); var budget = new Budget(); List<OfflineArtifactFact> facts = []; bool complete = true;
        List<Manifest> manifests = []; MinecraftInstanceMetadata metadata = new();
        string? selectedClient = null, selectedClientReference = null; Manifest? selectedClientManifest = null;
        try
        {
            string current = await FindManifestAsync(root, instance, Path.GetFileName(instance), budget, token).ConfigureAwait(false)
                ?? throw new IOException("本地版本 JSON 缺失。");
            HashSet<string> visited = new(Nexa.Core.PathIdentity.Comparer);
            string referenceId = Path.GetFileName(instance);
            while (true)
            {
                if (manifests.Count >= 32 || !visited.Add(current)) throw new IOException("版本继承循环或超过 32 层预算。");
                var json = await ReadJsonAsync(current, budget, 2 * 1024 * 1024, token).ConfigureAwait(false);
                manifests.Add(new(current, json)); facts.Add(new("manifest", Relative(root, current), OfflineArtifactState.Verified, new FileInfo(current).Length, "json-valid"));
                if (manifests.Count == 1) referenceId = json["id"]?.GetValue<string>() ?? referenceId;
                string? inherited = json["inheritsFrom"]?.GetValue<string>();
                string? reference = inherited ?? json["jar"]?.GetValue<string>();
                if (string.IsNullOrEmpty(reference) || inherited is null && string.Equals(reference, referenceId, StringComparison.OrdinalIgnoreCase)) break;
                if (!MinecraftVersionPaths.IsSafeReference(reference)) throw new IOException("版本继承引用无效。");
                referenceId = reference;
                current = await FindManifestAsync(root, Path.GetDirectoryName(current)!, reference, budget, token).ConfigureAwait(false)
                    ?? throw new IOException("继承版本 JSON 缺失。");
            }
            string metadataPath = Path.Combine(instance, MinecraftInstanceMetadataStore.MetadataDirectoryName, MinecraftInstanceMetadataStore.MetadataFileName);
            if (!File.Exists(metadataPath)) metadataPath = Path.Combine(instance, "PCL", MinecraftInstanceMetadataStore.MetadataFileName);
            if (File.Exists(metadataPath))
            {
                var json = await ReadJsonAsync(metadataPath, budget, 256 * 1024, token).ConfigureAwait(false);
                metadata = JsonSerializer.Deserialize(json.ToJsonString(), MinecraftJsonContext.Default.MinecraftInstanceMetadata) ?? throw new IOException("实例元数据无效。");
                if (metadata.SchemaVersion != 1) throw new IOException("实例元数据版本不支持。");
            }
            var effective = MinecraftLaunchPlanner.MergeManifests(manifests[0].Json, manifests.Skip(1).Select(x => x.Json).ToArray());
            if ((effective["libraries"] as JsonArray)?.Count > 16384) throw new IOException("版本依赖超过枚举预算。");
            var actualPlatform = platform ?? MinecraftLaunchPlatform.Detect();
            string nativeKey = actualPlatform.OperatingSystem switch
            { MinecraftLibraryOperatingSystem.Win32 => "windows", MinecraftLibraryOperatingSystem.Linux => "linux", MinecraftLibraryOperatingSystem.MacOs => "osx", _ => "unknown" };
            var libraryRequest = new MinecraftLibraryResolutionRequest
            {
                VersionJson = effective,
                MinecraftRootDirectory = root,
                TargetInstanceDirectory = instance,
                OperatingSystem = actualPlatform.OperatingSystem,
                OperatingSystemVersion = actualPlatform.OperatingSystemVersion,
                Is64BitArchitecture = actualPlatform.Is64BitArchitecture,
                IsArm64Architecture = actualPlatform.IsArm64Architecture,
                Features = new Dictionary<string, bool> { ["has_custom_resolution"] = true, ["is_demo_user"] = false }
            };
            var libraries = MinecraftLibraryResolver.Resolve(libraryRequest);
            List<Expected> expected = [];
            var baseManifest = manifests[^1];
            if (!MinecraftVersionPaths.IsSafeReference(referenceId)) throw new IOException("客户端版本身份无效。");
            string client = ProtocolJarPath(root, instance, referenceId, baseManifest, token) ?? throw new IOException("本地客户端 JAR 缺失或无法确定。");
            selectedClient = client; selectedClientReference = referenceId; selectedClientManifest = baseManifest;
            var download = baseManifest.Json["downloads"]?["client"];
            expected.Add(new("client", client, metadata.CorePatchSha256.Length == 64 ? null : Number(download?["size"]), download?["sha1"]?.GetValue<string>(),
                metadata.CorePatchSha256.Length == 64 ? metadata.CorePatchSha256 : null));
            foreach (var library in libraries) expected.Add(new(library.IsNatives ? "native" : "library", library.LocalPath, library.Size > 0 ? library.Size : null, library.Sha1));
            foreach (var item in expected.DistinctBy(x => x.Path, Nexa.Core.PathIdentity.Comparer)) facts.Add(await VerifyAsync(root, item, budget, token).ConfigureAwait(false));
            // Invalid allowed entries must not silently disappear through the resolver's safe omission.
            foreach (var value in effective["libraries"] as JsonArray ?? [])
            {
                if (value is not JsonObject entry || string.IsNullOrWhiteSpace(entry["name"]?.GetValue<string>()))
                { complete = false; facts.Add(new("library", "libraries", OfflineArtifactState.Unavailable, null, "invalid-library-metadata")); break; }
                var context = MinecraftRuleContext.From(actualPlatform.OperatingSystem, actualPlatform.OperatingSystemVersion,
                    actualPlatform.Is64BitArchitecture, actualPlatform.IsArm64Architecture, libraryRequest.Features);
                if (!MinecraftRuleEvaluator.IsAllowed(entry["rules"], context)) continue;
                var single = new JsonObject { ["libraries"] = new JsonArray(entry.DeepClone()) };
                var resolved = MinecraftLibraryResolver.Resolve(libraryRequest with { VersionJson = single });
                bool ordinaryRequired = entry["natives"] is null || entry["downloads"]?["artifact"] is not null;
                bool nativeRequired = entry["natives"] is JsonObject natives && !string.IsNullOrWhiteSpace(natives[nativeKey]?.GetValue<string>());
                int minimumArtifacts = (ordinaryRequired ? 1 : 0) + (nativeRequired ? 1 : 0);
                if (entry["natives"] is not null and not JsonObject || resolved.Count < minimumArtifacts
                    || nativeRequired && !resolved.Any(x => x.IsNatives))
                { complete = false; facts.Add(new("library", "libraries", OfflineArtifactState.Unavailable, null, "omitted-invalid-library")); }
            }
            JsonObject? indexMetadata = MinecraftAssetIndexResolver.ResolveIndex(new() { VersionJson = manifests[0].Json, InheritedVersionJsons = manifests.Skip(1).Select(x => x.Json).ToArray(), UseLegacyFallback = true }).IndexJson;
            string indexName = indexMetadata?["id"]?.GetValue<string>() ?? MinecraftAssetIndexResolver.LegacyIndexName;
            if (!MinecraftVersionPaths.IsSafeReference(indexName)) throw new IOException("资源索引身份无效。");
            string indexPath = Path.Combine(root, "assets", "indexes", indexName + ".json");
            var indexFact = await VerifyAsync(root, new("asset-index", indexPath, Number(indexMetadata?["size"]), indexMetadata?["sha1"]?.GetValue<string>()), budget, token).ConfigureAwait(false); facts.Add(indexFact);
            if (indexFact.State is OfflineArtifactState.Verified or OfflineArtifactState.PresentUnverified)
            {
                var index = await ReadJsonAsync(indexPath, budget, 8 * 1024 * 1024, token).ConfigureAwait(false);
                if (index["objects"] is not JsonObject objects || objects.Count + facts.Count > MaximumArtifacts) throw new IOException("资源对象缺失或超过枚举预算。");
                var assets = MinecraftAssetListResolver.GetAssetList(new() { IndexJson = index, MinecraftRootDirectory = root, InstanceDirectory = metadata.InstanceIsolation ? instance : root });
                foreach (var asset in assets.DistinctBy(x => x.LocalPath, Nexa.Core.PathIdentity.Comparer))
                {
                    if (asset.Hash.Length != 40 || asset.Hash.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("资源对象 SHA-1 无效。");
                    facts.Add(await VerifyAsync(root, new("asset", asset.LocalPath, asset.Size, asset.Hash), budget, token).ConfigureAwait(false));
                }
            }
        }
        catch (Exception error) when (ExpectedFailure(error))
        { complete = false; facts.Add(new("metadata", "versions/assets", OfflineArtifactState.Unavailable, null, "metadata-missing-invalid-or-budget")); }
        bool? javaCompatible = null; string? javaVersion = null;
        if (manifests.Count > 0 && selectJava is not null)
        {
            try
            {
                var requirement = JavaRequirement(manifests);
                var result = await selectJava(instance, requirement, metadata, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
                var selected = result.SelectedJava;
                javaCompatible = result.Success && selected is { IsAvailable: true, IsEnabled: true } && result.Requirement.Success
                    && result.Requirement.Range.Contains(selected.Installation.Version) && File.Exists(selected.Installation.JavaExecutablePath);
                if (javaCompatible == true && !OperatingSystem.IsWindows())
                {
                    var mode = File.GetUnixFileMode(selected!.Installation.JavaExecutablePath);
                    javaCompatible = (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
                }
                javaVersion = selected?.Installation.Version.ToString();
                facts.Add(new("java", "selected-runtime", javaCompatible == true ? OfflineArtifactState.Verified : OfflineArtifactState.Unavailable, null, javaCompatible == true ? "existing-compatible-runtime" : "missing-disabled-or-incompatible-runtime"));
            }
            catch (Exception error) when (ExpectedFailure(error)) { facts.Add(new("java", "selected-runtime", OfflineArtifactState.Unavailable, null, "runtime-check-unavailable")); }
        }
        else facts.Add(new("java", "selected-runtime", OfflineArtifactState.Unavailable, null, "runtime-provider-unavailable"));
        foreach (var (path, stamp) in budget.Stamps)
        {
            token.ThrowIfCancellationRequested(); var current = new FileInfo(path);
            if (!current.Exists || current.Length != stamp.Bytes || current.LastWriteTimeUtc.Ticks != stamp.Modified)
            { complete = false; facts.Add(new("capture", Relative(root, path), OfflineArtifactState.Unavailable, null, "changed-during-check")); break; }
        }
        if (selectedClient is not null && selectedClientManifest is not null && selectedClientReference is not null)
        {
            try
            {
                if (!Nexa.Core.PathIdentity.Comparer.Equals(selectedClient, ProtocolJarPath(root, instance, selectedClientReference, selectedClientManifest, token)))
                { complete = false; facts.Add(new("capture", Relative(root, selectedClient), OfflineArtifactState.Unavailable, null, "client-selection-changed")); }
            }
            catch (Exception error) when (ExpectedFailure(error))
            { complete = false; facts.Add(new("capture", Relative(root, selectedClient), OfflineArtifactState.Unavailable, null, "client-selection-unavailable")); }
        }
        return new InstanceOfflineReadinessReport(instance, DateTimeOffset.UtcNow, complete, javaCompatible, javaVersion, Array.AsReadOnly(facts.ToArray()));
    }, token);

    private static MinecraftJavaRequirementRequest JavaRequirement(List<Manifest> manifests)
    {
        var loader = MinecraftModLoaderDetector.Detect(manifests[0].Json); int? major = null; string? component = null; MinecraftGameVersion? game = null; DateTimeOffset? release = null;
        foreach (var item in manifests)
        {
            if (major is null && item.Json["javaVersion"] is JsonObject java) { major = java["majorVersion"]?.GetValue<int>(); component = java["component"]?.GetValue<string>(); }
            if (item.Json["id"] is { } id && MinecraftGameVersion.TryParse(id.GetValue<string>(), out var parsed)) game = parsed;
            if (release is null && DateTimeOffset.TryParse(item.Json["releaseTime"]?.GetValue<string>(), out var date)) release = date;
        }
        return new()
        {
            MinecraftVersion = game,
            HasReliableVanillaVersion = game is not null,
            ReleaseTime = release,
            ManifestJavaMajorVersion = major,
            ManifestJavaComponent = component,
            HasForge = loader.Kind is MinecraftModLoaderKind.Forge or MinecraftModLoaderKind.NeoForge,
            ForgeVersion = loader.Version,
            HasCleanroom = loader.Kind == MinecraftModLoaderKind.Cleanroom,
            CleanroomVersion = loader.Version ?? manifests[0].Json["cleanroom"]?.GetValue<string>(),
            HasFabric = loader.Kind is MinecraftModLoaderKind.Fabric or MinecraftModLoaderKind.Quilt,
            HasOptiFine = loader.Kind == MinecraftModLoaderKind.OptiFine,
            HasLiteLoader = loader.Kind == MinecraftModLoaderKind.LiteLoader,
            HasLabyMod = loader.Kind == MinecraftModLoaderKind.LabyMod
        };
    }

    private static async Task<OfflineArtifactFact> VerifyAsync(string root, Expected expected, Budget budget, CancellationToken token)
    {
        string relative = Relative(root, expected.Path); token.ThrowIfCancellationRequested();
        try
        {
            CheckLinks(expected.Path); var info = new FileInfo(expected.Path);
            if (!info.Exists) return new(expected.Category, relative, OfflineArtifactState.Missing, null, "file-missing");
            long size = info.Length;
            if (size is < 0 or > 512L * 1024 * 1024 || (budget.HashBytes = checked(budget.HashBytes + size)) > MaximumHashBytes) return new(expected.Category, relative, OfflineArtifactState.Unavailable, size, "hash-budget");
            budget.Stamps[expected.Path] = (size, info.LastWriteTimeUtc.Ticks);
            if (size == 0 || expected.Size is { } declared && size != declared) return new(expected.Category, relative, OfflineArtifactState.Corrupt, size, "length-mismatch");
            string? hash = expected.Sha256 ?? expected.Sha1;
            if (string.IsNullOrEmpty(hash)) return new(expected.Category, relative, OfflineArtifactState.PresentUnverified, size, "integrity-metadata-unavailable");
            int digits = expected.Sha256 is not null ? 64 : 40;
            if (hash.Length != digits || hash.Any(c => !char.IsAsciiHexDigit(c))) return new(expected.Category, relative, OfflineArtifactState.Unavailable, size, "integrity-metadata-invalid");
            await using var file = new FileStream(expected.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var digest = IncrementalHash.CreateHash(expected.Sha256 is not null ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
            byte[] buffer = new byte[81920]; long total = 0;
            while (true)
            { int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - total + 1)), token).ConfigureAwait(false); if (read == 0) break; total += read; if (total > size) return new(expected.Category, relative, OfflineArtifactState.Unavailable, size, "changed-during-check"); digest.AppendData(buffer.AsSpan(0, read)); }
            bool valid = total == size && Convert.ToHexString(digest.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase);
            return new(expected.Category, relative, valid ? OfflineArtifactState.Verified : OfflineArtifactState.Corrupt, size, valid ? "hash-matched" : "hash-mismatch");
        }
        catch (Exception error) when (ExpectedFailure(error)) { return new(expected.Category, relative, OfflineArtifactState.Unavailable, null, "read-unavailable"); }
    }

    private static async Task<JsonObject> ReadJsonAsync(string path, Budget budget, int maximumBytes, CancellationToken token)
    {
        CheckLinks(path); await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (file.Length > maximumBytes || (budget.JsonBytes = checked(budget.JsonBytes + file.Length)) > 16L * 1024 * 1024) throw new IOException("JSON 读取超过预算。");
        budget.Stamps[path] = (file.Length, File.GetLastWriteTimeUtc(path).Ticks);
        byte[] bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (file.ReadByte() != -1) throw new IOException("JSON 读取期间变化。");
        return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 48 }) as JsonObject ?? throw new IOException("JSON 文档无效。");
    }
    private static async Task<string?> FindManifestAsync(string root, string local, string reference, Budget budget, CancellationToken token)
    {
        foreach (string path in new[] { Path.Combine(root, "versions", reference, reference + ".json"), Path.Combine(local, reference + ".json") })
        { CheckLinks(path); if (File.Exists(path)) return path; }
        int visited = 0;
        foreach (string directory in Directory.EnumerateDirectories(Path.Combine(root, "versions")))
        {
            if (++visited > 1024) throw new IOException("版本发现超过预算。");
            CheckLinks(directory);
            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (++visited > 1024) throw new IOException("版本发现超过预算。");
                var candidate = await ReadJsonAsync(path, budget, 2 * 1024 * 1024, token).ConfigureAwait(false);
                if (candidate["id"]?.GetValue<string>() == reference) return path;
            }
        }
        return null;
    }
    private static long? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out long result) ? result : null;
    private static string Relative(string root, string path)
    { string value = Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/'); if (value.StartsWith("../", StringComparison.Ordinal) || value == ".." || Path.IsPathRooted(value)) throw new IOException("离线检查文件路径越界。"); return value; }
    private static void CheckLinks(string path) => RecoveryBlobStore.CheckLinks(path);
    private static bool ExpectedFailure(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException;
}
