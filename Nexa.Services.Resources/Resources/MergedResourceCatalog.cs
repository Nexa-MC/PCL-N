namespace Nexa.Services.Resources;

/// <summary>Partial provider failures do not discard healthy results. Identity comes from the curated index.</summary>
public sealed class MergedResourceCatalog(IResourceCatalogSource modrinth, IResourceCatalogSource curseForge) : IResourceCatalogSource, IResourceFileSource, IResourceChangelogSource
{
    private readonly ResourceSnapshotCache<ResourceSearchQuery, ResourceSearchResult> _searchCache = new();
    public Task<ResourceChangelog> ReadChangelogAsync(ResourceChangelogQuery query, CancellationToken token)
    {
        if (!Enum.IsDefined(query.Source.Provider)) throw new ArgumentException("资源站无效。");
        var source = query.Source.Provider == ResourceProvider.Modrinth ? modrinth : curseForge;
        return source is IResourceChangelogSource logs ? logs.ReadChangelogAsync(query, token) : Task.FromResult(new ResourceChangelog(""));
    }
    private readonly ResourceSnapshotCache<(string Project, string Game, string Loader, bool Mirror, ResourceReference? First, ResourceReference? Second), ResourceDetail> _detailCache = new();
    public Task<ResourceVersion?> ReadVersionAsync(ResourceDownloadCommand command, CancellationToken token)
    {
        if (!Enum.IsDefined(command.Provider)) throw new ArgumentException("资源站无效。");
        var source = command.Provider == ResourceProvider.Modrinth ? modrinth : curseForge;
        return source is IResourceFileSource files ? files.ReadVersionAsync(command, token) : throw new NotSupportedException("资源提供方不支持直接下载。");
    }
    public async Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_searchCache.TryRead(query, out var cached)) return cached!;
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
        if (string.IsNullOrEmpty(result.Notice)) _searchCache.Save(query, result);
        return result;
    }
    private static async Task<(ResourceSearchResult?, string?)> SearchOne(IResourceCatalogSource source, ResourceSearchQuery query, ResourceProvider provider, CancellationToken token)
    {
        try { return (await source.SearchAsync(query, token).ConfigureAwait(false), null); }
        catch (Exception e) when (!token.IsCancellationRequested && e is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        { return (null, provider + " 暂时不可用"); }
    }
    public async Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sources = query.Sources.Count == 0 ? new[] { new ResourceReference(ResourceProvider.Modrinth, query.ProjectId) } : query.Sources.Distinct().Take(2).ToArray();
        if (sources.Any(reference => !Enum.IsDefined(reference.Provider))) throw new ArgumentException("资源站无效。");
        var cacheKey = (query.ProjectId, query.GameVersion, query.Loader, query.MirrorFirst, sources.FirstOrDefault(), sources.Skip(1).FirstOrDefault());
        if (!query.Refresh && _detailCache.TryRead(cacheKey, out var cached)) return cached!;
        var results = await Task.WhenAll(sources.Select(async reference =>
        {
            try { return (Value: await (reference.Provider == ResourceProvider.Modrinth ? modrinth : curseForge).DetailAsync(query with { ProjectId = reference.ProjectId, Sources = [] }, token).ConfigureAwait(false), Error: (string?)null); }
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
        if (string.IsNullOrEmpty(result.Notice)) _detailCache.Save(cacheKey, result);
        return result;
    }
}
