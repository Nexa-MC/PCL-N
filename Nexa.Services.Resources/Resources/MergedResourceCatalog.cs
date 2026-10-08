namespace Nexa.Services.Resources;

/// <summary>Partial provider failures do not discard healthy results. Identity comes from the curated index.</summary>
public sealed class MergedResourceCatalog(IResourceCatalogSource modrinth, IResourceCatalogSource curseForge, ResourceOnlineInformationCache? information = null, Func<string>? sourcePolicyIdentity = null) : IResourceCatalogSource, IResourceFileSource, IResourceChangelogSource, IDisposable
{
    public MergedResourceCatalog(IResourceCatalogSource modrinth, IResourceCatalogSource curseForge) : this(modrinth, curseForge, null, null) { }
    private readonly ResourceOnlineInformationCache _information = information ?? new();
    public void Dispose() { if (information is null) _information.Dispose(); }
    private string Policy(bool mirror) => ResourceOnlineInformationCache.Identity(sourcePolicyIdentity?.Invoke() ?? "provider-default-v2", mirror.ToString());
    public Task<ResourceChangelog> ReadChangelogAsync(ResourceChangelogQuery query, CancellationToken token)
    {
        if (!Enum.IsDefined(query.Source.Provider)) throw new ArgumentException("资源站无效。");
        var source = query.Source.Provider == ResourceProvider.Modrinth ? modrinth : curseForge;
        return source is IResourceChangelogSource logs ? logs.ReadChangelogAsync(query, token) : Task.FromResult(new ResourceChangelog(""));
    }
    public async Task<ResourceVersion?> ReadVersionAsync(ResourceDownloadCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(command.Provider)) throw new ArgumentException("资源站无效。");
        var source = command.Provider == ResourceProvider.Modrinth ? modrinth : curseForge;
        if (source is not IResourceFileSource files) throw new NotSupportedException("资源提供方不支持直接下载。");
        string identity = ResourceOnlineInformationCache.Identity(Policy(command.MirrorFirst), command.Provider.ToString(), command.ProjectId, command.VersionId);
        var result = await _information.ReadAsync("version", identity, ResourceOnlineInformationCache.CatalogFresh, ResourceOnlineInformationCache.CatalogRetain,
            16384, ResourceMetadataJsonContext.Default.ResourceMetadataEnvelopeResourceVersionMetadata,
            async ct => new ResourceVersionMetadata(await files.ReadVersionAsync(command, ct).ConfigureAwait(false)),
            value => value.Version is { } version && ValidVersion(version) && version.Id == command.VersionId && version.Provider == command.Provider
                && (command.ProjectId.Length == 0 || version.ProjectId == command.ProjectId),
            value => new(value.Version is null ? null : ResourceOnlineInformationCache.Sanitize(value.Version)), value => value, refresh: command.Refresh, allowStale: !command.Refresh, token: token).ConfigureAwait(false);
        if (result.Version is { } version && (version.Id != command.VersionId || version.Provider != command.Provider
            || command.ProjectId.Length > 0 && version.ProjectId != command.ProjectId)) throw new InvalidDataException("资源版本身份不匹配。");
        return result.Version;
    }
    public Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string identity = ResourceOnlineInformationCache.Identity(Policy(query.MirrorFirst), query.Kind.ToString(), query.Text,
            query.GameVersion, query.Loader, query.Order.ToString(), query.Page.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return _information.ReadAsync("search", identity, ResourceOnlineInformationCache.CatalogFresh, ResourceOnlineInformationCache.CatalogRetain,
            65536, ResourceMetadataJsonContext.Default.ResourceMetadataEnvelopeResourceSearchResult,
            ct => SearchCoreAsync(query, ct), value => value.Projects.Count > 0 && value.Projects.All(ValidProject) && string.IsNullOrEmpty(value.Notice),
            ResourceOnlineInformationCache.Sanitize, value => value with { Notice = ResourceOnlineInformationCache.Notice(value.Notice), IsStale = true }, refresh: query.Refresh, waitForRefresh: query.WaitForRefresh, token: token);
    }
    private async Task<ResourceSearchResult> SearchCoreAsync(ResourceSearchQuery query, CancellationToken token)
    {
        var index = await Task.Run(() => ChineseResourceIndex.Shared.Value, token).ConfigureAwait(false);
        var matches = index.Find(query.Text);
        var calls = new List<Task<(ResourceSearchResult? Value, string? Error)>>();
        foreach (var provider in new[] { ResourceProvider.Modrinth, ResourceProvider.CurseForge })
        {
            var source = provider == ResourceProvider.Modrinth ? modrinth : curseForge;
            var terms = matches.Select(m => provider == ResourceProvider.Modrinth ? m.Modrinth : m.CurseForge).Where(s => s.Length > 0).Distinct().ToArray();
            if (terms.Length == 0) terms = [query.Text];
            foreach (string term in terms) calls.Add(SearchOne(source, query with { Text = term }, provider, token));
        }
        var results = await Task.WhenAll(calls).ConfigureAwait(false);
        if (results.All(r => r.Value is null)) throw new IOException("暂时无法连接资源站。");
        var projects = new List<ResourceProject>();
        var identities = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in results.Where(r => r.Value is not null).SelectMany(r => r.Value!.Projects).Select(index.Decorate))
        {
            string key = index.MergeKey(p);
            if (!identities.TryGetValue(key, out int existing)) { identities[key] = projects.Count; projects.Add(p); }
            else projects[existing] = projects[existing] with { Sources = projects[existing].Sources.Concat(p.Sources).Distinct().ToArray(), IsLibrary = projects[existing].IsLibrary || p.IsLibrary };
        }
        var result = new ResourceSearchResult(projects, results.Sum(r => r.Value?.Total ?? 0), query.Page)
        { Notice = string.Join("；", results.Select(r => r.Error).Where(s => s is not null).Distinct()), HasMore = results.Any(r => r.Value is { } value && (query.Page + 1) * 20 < value.Total) };
        token.ThrowIfCancellationRequested();
        return result;
    }
    private static async Task<(ResourceSearchResult?, string?)> SearchOne(IResourceCatalogSource source, ResourceSearchQuery query, ResourceProvider provider, CancellationToken token)
    {
        try { return (await source.SearchAsync(query, token).ConfigureAwait(false), null); }
        catch (Exception e) when (!token.IsCancellationRequested && e is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        { return (null, provider + " 暂时不可用"); }
    }
    public Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sources = Sources(query);
        string identity = ResourceOnlineInformationCache.Identity(Policy(query.MirrorFirst), query.ProjectId, query.GameVersion, query.Loader,
            string.Join(",", sources.Select(source => source.Provider + ":" + source.ProjectId).Order(StringComparer.Ordinal)));
        return _information.ReadAsync("detail", identity, ResourceOnlineInformationCache.CatalogFresh, ResourceOnlineInformationCache.CatalogRetain,
            1024 * 1024, ResourceMetadataJsonContext.Default.ResourceMetadataEnvelopeResourceDetail,
            ct => DetailCoreAsync(query, ct), value => ValidProject(value.Project) && sources.Any(source => source.ProjectId == value.Project.Id)
                && value.Project.Sources.All(sources.Contains) && value.Versions.All(version => ValidVersion(version) && sources.Contains(new(version.Provider, version.ProjectId)))
                && string.IsNullOrEmpty(value.Notice), ResourceOnlineInformationCache.Sanitize,
            value => value with { Notice = ResourceOnlineInformationCache.Notice(value.Notice), IsStale = true }, refresh: query.Refresh, waitForRefresh: query.WaitForRefresh, token: token);
    }
    private static bool ValidReference(ResourceReference reference) => Enum.IsDefined(reference.Provider) && reference.ProjectId.Length is > 0 and <= 64
        && (reference.Provider == ResourceProvider.Modrinth ? reference.ProjectId.All(char.IsAsciiLetterOrDigit) : reference.ProjectId.Length < 20 && reference.ProjectId.All(char.IsAsciiDigit));
    private static bool ValidProject(ResourceProject project) => Enum.IsDefined(project.Kind) && project.Sources.Count is > 0 and <= 2
        && project.Sources.All(ValidReference) && project.Sources.Any(source => source.ProjectId == project.Id);
    private static bool ValidVersion(ResourceVersion version) => ValidReference(new(version.Provider, version.ProjectId))
        && version.Id.Length is > 0 and <= 64 && (version.Provider == ResourceProvider.Modrinth ? version.Id.All(char.IsAsciiLetterOrDigit)
            : version.Id.Length < 20 && version.Id.All(char.IsAsciiDigit));
    private static ResourceReference[] Sources(ResourceDetailQuery query) => query.Sources.Count == 0
        ? [new(ResourceProvider.Modrinth, query.ProjectId)] : query.Sources.Distinct().Take(2).ToArray();
    private async Task<ResourceDetail> DetailCoreAsync(ResourceDetailQuery query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sources = Sources(query);
        if (sources.Any(reference => !Enum.IsDefined(reference.Provider))) throw new ArgumentException("资源站无效。");
        var results = await Task.WhenAll(sources.Select(async reference =>
        {
            try
            {
                var detail = await (reference.Provider == ResourceProvider.Modrinth ? modrinth : curseForge).DetailAsync(query with { ProjectId = reference.ProjectId, Sources = [] }, token).ConfigureAwait(false);
                if (detail.Project.Id != reference.ProjectId || detail.Versions.Any(version => version.Provider != reference.Provider || version.ProjectId != reference.ProjectId))
                    throw new InvalidDataException("资源详情不属于此提供方项目。");
                return (Value: detail, Error: (string?)null);
            }
            catch (Exception e) when (!token.IsCancellationRequested && e is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
            { return (Value: (ResourceDetail?)null, Error: reference.Provider + " 暂时不可用"); }
        })).ConfigureAwait(false);
        var details = results.Where(r => r.Value is not null).Select(r => r.Value!).ToArray();
        if (details.Length == 0) throw new IOException("暂时无法加载资源详情。");
        var index = await Task.Run(() => ChineseResourceIndex.Shared.Value, token).ConfigureAwait(false);
        var versions = new List<ResourceVersion>();
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in details.SelectMany(d => d.Versions).OrderByDescending(v => v.Published, StringComparer.Ordinal))
        {
            if (v.File is { } f)
            {
                string? digest = f.Sha512 is { Length: 128 } sha512 && sha512.All(char.IsAsciiHexDigit) ? "512:" + sha512
                    : f.Sha1 is { Length: 40 } sha1 && sha1.All(char.IsAsciiHexDigit) ? "1:" + sha1 : null;
                if (digest is not null && !hashes.Add(f.Size + ":" + digest)) continue;
            }
            versions.Add(v);
        }
        var result = new ResourceDetail(index.Decorate(details[0].Project) with { Sources = sources }, details[0].License, versions)
        { Notice = string.Join("；", results.Select(r => r.Error ?? r.Value?.Notice).Where(s => s is not null)) };
        token.ThrowIfCancellationRequested();
        return result;
    }
}
