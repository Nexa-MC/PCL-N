using Nexa.Services.Foundation;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Resources;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

public sealed class ResourceCatalogRuntime(XsrQueryRouter queries, HttpClient? ownedHttp, ResourceIconService? icons = null,
    ResourceOnlineInformationCache? ownedInformation = null) : IDisposable
{
    public ResourceCatalogRuntime(XsrQueryRouter queries, HttpClient? ownedHttp, ResourceIconService? icons = null)
        : this(queries, ownedHttp, icons, null) { }
    public XsrCommandRouter? Commands { get; init; }
    public XsrQueryRouter Queries { get; } = queries;
    public void Dispose() { ownedHttp?.Dispose(); icons?.Dispose(); ownedInformation?.Dispose(); }
}

public static class ResourceCatalogRuntimeComposer
{
    public static ResourceCatalogRuntime Compose(IResourceCatalogSource? source = null, IXsrDispatchObserver? observer = null, FoundationHost? host = null, string? favoritesPath = null, MinecraftInstallService? installer = null)
    {
        HttpClient? http = source is null ? host?.CreateHttpClient(allowAutoRedirect: false)
            ?? Nexa.Services.Downloads.PooledHttpClient.Create(allowAutoRedirect: false) : null;
        if (http is not null) http.Timeout = TimeSpan.FromSeconds(25);
        string SourcePriority() => host?.SettingsPolicy.Read(new()).Value?.Values.FirstOrDefault(item => item.Key == "network.resource-source")?.Value.Value ?? "follow-request";
        bool AutoDependencies() => host?.SettingsPolicy.Read(new()).Value?.Values.FirstOrDefault(item => item.Key == "network.auto-install-dependencies")?.Value.Value != "false";
        var transport = http is null ? null : new ResourceProviderHttp(http) { SourcePriority = SourcePriority };
        var information = new ResourceOnlineInformationCache(host?.SharedStateCache,
            host?.CacheDirectory is null ? null : Path.Combine(host.CacheDirectory, "resource-information"));
        source ??= new MergedResourceCatalog(new ResourceCatalogService(http!, transport), new CurseForgeResourceCatalog(transport!),
            information, () => transport!.CachePolicyIdentity);
        var translations = transport is null ? null : new ResourceTranslationService(transport, information);
        ResourceIconService? icons = http is null ? null : new(http, host?.SharedStateCache) { WorkScheduler = host?.Work };
        XsrQueryRouterBuilder queries = new();
        queries.Register<ResourceNetworkPolicyQuery, ResourceNetworkPolicySnapshot>(ResourceCatalogContract.NetworkPolicy,
            (_, _) => ValueTask.FromResult(XsrResult.Success(new ResourceNetworkPolicySnapshot(SourcePriority(), AutoDependencies(), transport?.LastResolution))));
        queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search,
            async (query, token) => XsrResult.Success(await source.SearchAsync(query, token).ConfigureAwait(false)));
        queries.Register<ResourceDetailQuery, ResourceDetail>(ResourceCatalogContract.Detail,
            async (query, token) => XsrResult.Success(await source.DetailAsync(query, token).ConfigureAwait(false)));
        queries.Register<ResourceChangelogQuery, ResourceChangelog>(ResourceCatalogContract.Changelog,
            async (query, token) => XsrResult.Success(source is IResourceChangelogSource logs ? await logs.ReadChangelogAsync(query, token).ConfigureAwait(false) : new ResourceChangelog("")));
        queries.Register<ResourceIconQuery, ResourceIconResult>(ResourceCatalogContract.Icon,
            async (query, token) => XsrResult.Success(icons is null ? new ResourceIconResult(null) : await icons.ReadAsync(query, token).ConfigureAwait(false)));
        queries.Register<ResourceTranslationQuery, ResourceTranslation>(ResourceCatalogContract.Translate,
            async (query, token) => XsrResult.Success(translations is null ? new ResourceTranslation(null) : await translations.ReadAsync(query, token).ConfigureAwait(false)));
        var favorites = new ResourceFavoritesService(favoritesPath);
        queries.Register<ResourceFavoritesQuery, ResourceFavoritesSnapshot>(ResourceCatalogContract.Favorites,
            async (query, token) => XsrResult.Success(await favorites.ReadAsync(token).ConfigureAwait(false)));
        XsrCommandRouterBuilder commands = new();
        commands.Register<ResourceFavoriteCommand>(ResourceCatalogContract.Favorite, async (command, token) =>
        { await favorites.SetAsync(command, token).ConfigureAwait(false); return XsrResult.Success(); });
        if (host is not null && http is not null)
        {
            var downloader = new ResourceDownloadService(source, host.Downloads, host.Tasks, http) { SourcePriority = SourcePriority };
            var instances = new ResourceInstanceService(transport!, information);
            var content = new ResourceContentOnlineService(instances, source, translations);
            queries.Register<ResourceContentOnlineQuery, ResourceContentOnline>(ResourceCatalogContract.ContentOnline,
                async (query, token) => XsrResult.Success(await content.ReadAsync(query, token).ConfigureAwait(false)));
            queries.Register<ResourceContentOnlineBatchQuery, ResourceContentOnlineBatch>(ResourceCatalogContract.ContentOnlineBatch,
                async (query, token) => XsrResult.Success(await content.ReadBatchAsync(query, token).ConfigureAwait(false)));
            var updates = new ResourceContentUpdateService(content, downloader, host.StateStore);
            commands.Register<ResourceContentUpdateCommand>(ResourceCatalogContract.UpdateContent, async (command, token) =>
            {
                try { await updates.UpdateAsync(command, token).ConfigureAwait(false); return XsrResult.Success(); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                { return XsrResult.Failure(new XsrError(XsrErrorKind.Rejected, XsrSemanticId.Parse("resources.update.failed"), error.Message)); }
            });
            commands.Register<ResourceContentUpdateBatchCommand>(ResourceCatalogContract.UpdateContentBatch, async (command, token) =>
            {
                try { await updates.UpdateBatchAsync(command, token).ConfigureAwait(false); return XsrResult.Success(); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or AggregateException)
                { return XsrResult.Failure(new XsrError(XsrErrorKind.Rejected, XsrSemanticId.Parse("resources.update.failed"), error.Message)); }
            });
            queries.Register<ResourceInstanceQuery, ResourceInstanceContext>(ResourceCatalogContract.Instance,
                async (query, token) => XsrResult.Success(await instances.ReadAsync(query, token).ConfigureAwait(false)));
            queries.Register<ResourceModPlanQuery, ResourceInstallPlan>(ResourceCatalogContract.PlanMod, async (query, token) =>
            {
                try
                {
                    return XsrResult.Success(await new ResourceDependencyPlanner(source) { AutoInstallDependencies = AutoDependencies() }
                    .PreviewAsync(query.Command, await instances.ReadAsync(query.Command.Instance, token).ConfigureAwait(false), token).ConfigureAwait(false));
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                { return XsrResult.Failure<ResourceInstallPlan>(new XsrError(XsrErrorKind.Rejected, XsrSemanticId.Parse("resources.plan.failed"), error.Message)); }
            });
            if (installer is not null)
            {
                var mods = new ResourceModInstallService(source, instances, downloader, new MinecraftLocalJarService(host.Tasks, host.StateStore, installer), host.Tasks)
                { AutoInstallDependencies = AutoDependencies };
                commands.Register<ResourceModInstallCommand>(ResourceCatalogContract.InstallMod, async (command, token) =>
                {
                    try { await mods.InstallAsync(command, token).ConfigureAwait(false); return XsrResult.Success(); }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                    { return XsrResult.Failure(new XsrError(XsrErrorKind.Rejected, XsrSemanticId.Parse("resources.install.failed"), error.Message)); }
                });
            }
            commands.Register<ResourceDownloadCommand>(ResourceCatalogContract.Download, async (command, token) =>
            {
                try { await downloader.DownloadAsync(command, token).ConfigureAwait(false); return XsrResult.Success(); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                { return XsrResult.Failure(new XsrError(XsrErrorKind.Rejected, XsrSemanticId.Parse("resources.download.failed"), error.Message)); }
            });
        }
        return new(queries.Build(observer ?? new Observer()), http, icons, information) { Commands = commands.Build(observer ?? new Observer()) };
    }
    private sealed class Observer : IXsrDispatchObserver { public void OnCompleted(XsrDispatchObservation observation) { } }
}
