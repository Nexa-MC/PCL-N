using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;

namespace Nexa.Services.Resources;

public sealed class ResourceContentOnlineService(ResourceInstanceService instances, IResourceCatalogSource catalog, ResourceTranslationService? translations = null)
{
    public Task<ResourceContentOnlineBatch> ReadBatchAsync(ResourceContentOnlineBatchQuery query, CancellationToken token) => Task.Run(async () =>
    {
        if (query.Files.Count > 100) throw new InvalidDataException("一次最多识别 100 个资源文件。");
        long budget = 1024L * 1024 * 1024;
        Dictionary<ResourceContentOnlineQuery, ResourceInstanceService.Fingerprint> eligible = [];
        Dictionary<ResourceContentOnlineQuery, ResourceIdentityMetadata> identified = [];
        foreach (var group in query.Files.GroupBy(q => q.MirrorFirst))
        {
            List<ResourceInstanceService.Fingerprint> fingerprints = [];
            foreach (var item in group)
            {
                token.ThrowIfCancellationRequested();
                if (item.PageId is not ("mods" or "resourcepacks" or "shaderpacks") || !Path.IsPathFullyQualified(item.InstanceDirectory)
                    || !MinecraftVersionPaths.IsSafeReference(item.Name) || item.ExpectedSize is < 0 or > 512L * 1024 * 1024) continue;
                if (item.ExpectedSize > budget) continue;
                budget -= item.ExpectedSize;
                try
                {
                    string instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.InstanceDirectory));
                    var versions = Directory.GetParent(instance);
                    if (versions?.Name != "versions" || versions.Parent is null) continue;
                    CheckLinks(instance);
                    var metadata = await new MinecraftInstanceMetadataStore().LoadAsync(instance, token).ConfigureAwait(false);
                    var file = new FileInfo(Path.Combine(metadata.InstanceIsolation ? instance : versions.Parent.FullName, item.PageId, item.Name));
                    CheckLinks(file.FullName); CheckIdentity(file, item);
                    var fingerprint = await instances.ReadFingerprintAsync(file, token).ConfigureAwait(false);
                    if (fingerprint is not null) { fingerprints.Add(fingerprint); eligible[item] = fingerprint; }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            var identities = await instances.IdentifyManyAsync(fingerprints, group.Key, token, query.Refresh, query.WaitForRefresh).ConfigureAwait(false);
            foreach (var item in group)
                if (eligible.TryGetValue(item, out var fingerprint) && identities.TryGetValue(fingerprint.Sha512, out var identity))
                    identified[item] = identity;
        }
        var matches = new ResourceContentOnlineMatch[query.Files.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, query.Files.Count), new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token }, async (i, ct) =>
        {
            var file = query.Files[i];
            if (!identified.TryGetValue(file, out var identity)) { matches[i] = new(file, new(null, null, [], "文件已变化或超过本次识别预算，已保留本地资料。")); return; }
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                var content = await ReadCoreAsync(file with { WaitForRefresh = query.WaitForRefresh }, identity, query.Refresh, deadline.Token).ConfigureAwait(false);
                matches[i] = new(file, content with { Versions = content.Versions.Take(1).ToArray() });
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
            { matches[i] = new(file, new(null, null, [], "暂时无法识别在线信息，已保留本地资料。")); }
        }).ConfigureAwait(false);
        return new ResourceContentOnlineBatch(Array.AsReadOnly(matches));
    }, token);
    public Task<ResourceContentOnline> ReadAsync(ResourceContentOnlineQuery query, CancellationToken token) => ReadCoreAsync(query, null, query.Refresh, token);
    private Task<ResourceContentOnline> ReadCoreAsync(ResourceContentOnlineQuery query, ResourceIdentityMetadata? preloaded, bool refresh, CancellationToken token) => Task.Run(async () =>
    {
        ResourceKind kind = query.PageId switch
        {
            "mods" => ResourceKind.Mod,
            "resourcepacks" => ResourceKind.ResourcePack,
            "shaderpacks" => ResourceKind.Shader,
            _ => throw new InvalidDataException("此内容不支持在线识别。")
        };
        if (!Path.IsPathFullyQualified(query.InstanceDirectory) || !MinecraftVersionPaths.IsSafeReference(query.Name))
            throw new InvalidDataException("请选择有效的资源文件。");
        string instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(query.InstanceDirectory));
        var versions = Directory.GetParent(instance);
        if (versions?.Name != "versions" || versions.Parent is null || !MinecraftVersionPaths.IsSafeReference(Path.GetFileName(instance)))
            throw new InvalidDataException("请选择有效的版本目录。");
        CheckLinks(instance);
        var metadata = await new MinecraftInstanceMetadataStore().LoadAsync(instance, token).ConfigureAwait(false);
        string game = metadata.InstanceIsolation ? instance : versions.Parent.FullName;
        var file = new FileInfo(Path.Combine(game, query.PageId, query.Name));
        CheckLinks(file.FullName);
        CheckIdentity(file, query);
        if (file.Length > 512L * 1024 * 1024) return new ResourceContentOnline(null, null, [], "文件较大，暂不进行在线识别。");
        var fingerprint = await instances.ReadFingerprintAsync(file, token).ConfigureAwait(false);
        if (fingerprint is null) throw new IOException("文件已变化，请刷新后重试。");
        var identified = preloaded ?? await instances.IdentifyAsync(fingerprint, query.MirrorFirst, token, refresh, query.WaitForRefresh).ConfigureAwait(false);
        if (preloaded is not null) identified = identified with { Files = identified.Files.Select(match => match with { FileName = file.Name, Enabled = fingerprint.Enabled }).ToArray() };
        if (identified.Files.Length == 0) return new ResourceContentOnline(null, null, [], identified.Complete ? "模组站尚未收录此文件，已保留本地信息。" : "暂时无法连接模组站，已保留本地信息。");
        var edit = await MinecraftInstallEditService.ReadAsync(new(versions.Parent.FullName, Path.GetFileName(instance)), token).ConfigureAwait(false);
        string loader = kind == ResourceKind.Mod ? edit.Selection.Select(selection => selection.Loader switch
        { InstallLoader.Fabric or InstallLoader.LegacyFabric => "fabric", InstallLoader.Quilt => "quilt", InstallLoader.Forge or InstallLoader.Cleanroom => "forge", InstallLoader.NeoForge => "neoforge", _ => "" }).FirstOrDefault(value => value.Length > 0) ?? "" : "";
        var sources = identified.Files.Select(match => match.Source).Distinct().ToArray();
        var detail = await catalog.DetailAsync(new(sources[0].ProjectId, edit.GameVersion, loader) { Sources = sources, MirrorFirst = query.MirrorFirst, Refresh = refresh, WaitForRefresh = query.WaitForRefresh }, token).ConfigureAwait(false);
        var project = detail.Project with { Kind = kind };
        string? translationNotice = null;
        // List batches only project name/version/update facts. Translate descriptions when the detail consumes them.
        if (preloaded is null && translations is not null && string.IsNullOrWhiteSpace(project.ChineseDescription))
        {
            var translated = await translations.ReadAsync(new(sources[0], project.Description), token).ConfigureAwait(false);
            project = project with { ChineseDescription = translated.Description };
            translationNotice = translated.Notice;
        }
        List<ResourceVersion> installed = [];
        bool versionUnavailable = false;
        foreach (var match in identified.Files)
        {
            var version = detail.Versions.FirstOrDefault(v => v.Id == match.VersionId && v.Provider == match.Source.Provider && v.ProjectId == match.Source.ProjectId);
            if (version is null && catalog is IResourceFileSource files)
                try { version = await files.ReadVersionAsync(new(match.Source.Provider, match.Source.ProjectId, match.VersionId, "", query.MirrorFirst), token).ConfigureAwait(false); }
                catch (Exception error) when (!token.IsCancellationRequested && ResourceOnlineInformationCache.Recoverable(error)) { versionUnavailable = true; }
            if (version is not null && version.Id == match.VersionId && version.Provider == match.Source.Provider && version.ProjectId == match.Source.ProjectId)
                installed.Add(version);
        }
        string? installedName = installed.FirstOrDefault()?.Number;
        ResourceVersion? update = null;
        bool? updateAvailable = null;
        // Display numbers and filenames are not ordered version identities. Unknown chronology
        // remains unknown; identical bytes on a second provider are never offered as an update.
        if (edit.GameVersion.Length > 0 && (kind != ResourceKind.Mod || loader.Length > 0)
            && installed.Count == identified.Files.Length && installed.All(v => Publication(v) is not null))
        {
            var latestInstalled = installed.Max(v => Publication(v)!.Value);
            update = detail.Versions.Where(v => v.File is not null && sources.Contains(new(v.Provider, v.ProjectId))
                && v.Games.Contains(edit.GameVersion, StringComparer.OrdinalIgnoreCase)
                && (kind != ResourceKind.Mod || loader.Length > 0 && v.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase))
                && !identified.Files.Any(f => f.Source.Provider == v.Provider && f.Source.ProjectId == v.ProjectId && f.VersionId == v.Id)
                && !string.Equals(v.File.Sha512, fingerprint.Sha512, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(v.File.Sha1, fingerprint.Sha1, StringComparison.OrdinalIgnoreCase)
                && Publication(v) is { } published && published > latestInstalled)
                .OrderByDescending(v => Publication(v)).FirstOrDefault();
            updateAvailable = update is not null ? true : identified.Complete && string.IsNullOrWhiteSpace(detail.Notice) ? false : null;
        }
        file.Refresh(); CheckIdentity(file, query); CheckLinks(file.FullName);
        bool stale = identified.IsStale || detail.IsStale || detail.Notice?.Contains(ResourceOnlineInformationCache.StaleNotice, StringComparison.Ordinal) == true;
        string? notice = identified.IsStale ? ResourceOnlineInformationCache.Notice(detail.Notice) : detail.Notice;
        if (!identified.Complete || versionUnavailable) notice = string.IsNullOrEmpty(notice) ? "部分来源暂时无法连接。" : notice + " 部分来源暂时无法连接。";
        if (translationNotice is not null) notice = string.IsNullOrEmpty(notice) ? translationNotice : notice + " " + translationNotice;
        var versionsWithInstalled = installed.Concat(detail.Versions).DistinctBy(version => (version.Provider, version.ProjectId, version.Id)).ToArray();
        return new ResourceContentOnline(project, installedName, versionsWithInstalled, notice)
        { InstalledFiles = Array.AsReadOnly(identified.Files), UpdateVersion = stale ? null : update, UpdateAvailable = stale ? null : updateAvailable, IsStale = stale };
    }, token);

    private static DateTimeOffset? Publication(ResourceVersion version) => DateTimeOffset.TryParse(version.Published,
        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var value) ? value : null;

    private static void CheckIdentity(FileInfo file, ResourceContentOnlineQuery query)
    {
        if (!file.Exists || file.Length != query.ExpectedSize || file.LastWriteTimeUtc.Ticks != query.ExpectedModifiedUtcTicks)
            throw new IOException("文件已变化，请刷新后重试。");
    }
    private static void CheckLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能识别链接目录中的资源。");
    }
}
