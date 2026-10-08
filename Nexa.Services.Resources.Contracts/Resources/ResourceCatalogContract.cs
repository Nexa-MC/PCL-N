using Nexa.Xsr;

namespace Nexa.Services.Resources;

public static class ResourceCatalogContract
{
    public static readonly XsrSemanticId Search = XsrSemanticId.Parse("resources.catalog.search");
    public static readonly XsrSemanticId Detail = XsrSemanticId.Parse("resources.catalog.detail");
    public static readonly XsrSemanticId Icon = XsrSemanticId.Parse("resources.catalog.icon");
    public static readonly XsrSemanticId Translate = XsrSemanticId.Parse("resources.catalog.translate");
    public static readonly XsrSemanticId Download = XsrSemanticId.Parse("resources.catalog.download");
    public static readonly XsrSemanticId Instance = XsrSemanticId.Parse("resources.instance.read");
    public static readonly XsrSemanticId PlanMod = XsrSemanticId.Parse("resources.mod.plan");
    public static readonly XsrSemanticId InstallMod = XsrSemanticId.Parse("resources.mod.install");
    public static readonly XsrSemanticId Favorites = XsrSemanticId.Parse("resources.favorites.read");
    public static readonly XsrSemanticId Favorite = XsrSemanticId.Parse("resources.favorites.set");
    public static readonly XsrSemanticId ContentOnline = XsrSemanticId.Parse("resources.content.online");
    public static readonly XsrSemanticId ContentOnlineBatch = XsrSemanticId.Parse("resources.content.online-batch");
    public static readonly XsrSemanticId UpdateContent = XsrSemanticId.Parse("resources.content.update");
    public static readonly XsrSemanticId UpdateContentBatch = XsrSemanticId.Parse("resources.content.update-batch");
    public static readonly XsrSemanticId Changelog = XsrSemanticId.Parse("resources.catalog.changelog");
    public static readonly XsrSemanticId NetworkPolicy = XsrSemanticId.Parse("resources.network.policy");
    public const int PageSize = 20;
}

public enum ResourceKind { Mod, Modpack, ResourcePack, Shader, DataPack }
public enum ResourceOrder { Relevance, Downloads, Updated }
public enum ResourceProvider { Modrinth, CurseForge }
public sealed record ResourceReference(ResourceProvider Provider, string ProjectId);
public sealed record ResourceSearchQuery(ResourceKind Kind = ResourceKind.Mod, string Text = "",
    string GameVersion = "", string Loader = "", ResourceOrder Order = ResourceOrder.Relevance, int Page = 0)
{ public bool MirrorFirst { get; init; } = true; }
public sealed record ResourceProject(string Id, string Title, string Description, string Author,
    long Downloads, string Website)
{
    public string? IconUrl { get; init; }
    public ResourceKind Kind { get; init; }
    public bool IsLibrary { get; init; }
    public string Slug { get; init; } = "";
    public string? ChineseName { get; init; }
    public string? ChineseDescription { get; init; }
    public int? WikiId { get; init; }
    public IReadOnlyList<ResourceReference> Sources { get; init; } = [];
    public string DisplayName => string.IsNullOrWhiteSpace(ChineseName) ? Title : ChineseName;
    public string DisplayDescription => string.IsNullOrWhiteSpace(ChineseDescription) ? Description : ChineseDescription;
    public string SourceLabel => Sources.Any(item => item.Provider == ResourceProvider.CurseForge)
        ? Sources.Any(item => item.Provider == ResourceProvider.Modrinth) ? "Modrinth · CurseForge" : "CurseForge" : "Modrinth";
}
public sealed record ResourceIconQuery(string Url);
public sealed record ResourceIconResult(Nexa.Core.Media.PngImage? Image);
public sealed record ResourceSearchResult(IReadOnlyList<ResourceProject> Projects, int Total, int Page)
{ public string? Notice { get; init; } public bool? HasMore { get; init; } }
public sealed record ResourceDetailQuery(string ProjectId, string GameVersion = "", string Loader = "")
{ public IReadOnlyList<ResourceReference> Sources { get; init; } = []; public bool MirrorFirst { get; init; } = true; public bool Refresh { get; init; } }
public sealed record ResourceVersion(string Id, string Name, string Number, string Channel,
    IReadOnlyList<string> Games, IReadOnlyList<string> Loaders, string Published, string Website)
{
    public ResourceProvider Provider { get; init; }
    public string ProjectId { get; init; } = "";
    public ResourceFile? File { get; init; }
    public IReadOnlyList<ResourceDependency> Dependencies { get; init; } = [];
    public string Changelog { get; init; } = "";
}
public sealed record ResourceFile(string Name, string Url, long Size, string? Sha1, string? Sha512);
public sealed record ResourceDetail(ResourceProject Project, string License, IReadOnlyList<ResourceVersion> Versions)
{ public string? Notice { get; init; } }
public sealed record ResourceTranslationQuery(ResourceReference Source, string Original);
public sealed record ResourceTranslation(string? Description);
public sealed record ResourceDownloadCommand(ResourceProvider Provider, string ProjectId, string VersionId, string DestinationDirectory, bool MirrorFirst = true);

public interface IResourceCatalogSource
{
    Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token);
    Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token);
}

public interface IResourceFileSource
{
    Task<ResourceVersion?> ReadVersionAsync(ResourceDownloadCommand command, CancellationToken token);
    async Task<ResourceFile?> ReadFileAsync(ResourceDownloadCommand command, CancellationToken token) => (await ReadVersionAsync(command, token).ConfigureAwait(false))?.File;
}

public sealed record ResourceDependency(string ProjectId, string? VersionId, string Kind);
public sealed record ResourceInstanceQuery(string Root, string InstanceId, bool MirrorFirst = true);
public sealed record ResourceInstalledFile(ResourceReference Source, string VersionId, string FileName, string Sha512, bool Enabled);
public sealed record ResourceInstanceContext(string Game, string Loader, IReadOnlyList<ResourceInstalledFile> Installed, string? Notice)
{ public string GameDirectory { get; init; } = ""; }
public sealed record ResourceModInstallCommand(ResourceReference Source, string? VersionId, ResourceInstanceQuery Instance)
{ public IReadOnlyList<ResourceReference> OptionalDependencies { get; init; } = []; }
public sealed record ResourceModPlanQuery(ResourceModInstallCommand Command);
public sealed record ResourceOptionalDependency(ResourceReference Source, string? VersionId, string Name);
public sealed record ResourceInstallPlan(IReadOnlyList<ResourceVersion> Versions, IReadOnlyList<ResourceOptionalDependency> Optional, bool HasCycles);
public sealed record ResourceFavoritesQuery;
public sealed record ResourceFavoritesSnapshot(IReadOnlyList<ResourceProject> Projects);
public sealed record ResourceFavoriteCommand(ResourceProject Project, bool Saved);
public sealed record ResourceContentOnlineQuery(string InstanceDirectory, string PageId, string Name, long ExpectedSize, long ExpectedModifiedUtcTicks)
{ public bool MirrorFirst { get; init; } = true; }
public sealed record ResourceContentOnline(ResourceProject? Project, string? InstalledVersion, IReadOnlyList<ResourceVersion> Versions, string? Notice)
{
    public IReadOnlyList<ResourceInstalledFile> InstalledFiles { get; init; } = [];
    public ResourceVersion? UpdateVersion { get; init; }
    public bool? UpdateAvailable { get; init; }
}
public sealed record ResourceContentOnlineBatchQuery(IReadOnlyList<ResourceContentOnlineQuery> Files)
{ public bool Refresh { get; init; } }
public sealed record ResourceContentOnlineMatch(ResourceContentOnlineQuery File, ResourceContentOnline Content);
public sealed record ResourceContentOnlineBatch(IReadOnlyList<ResourceContentOnlineMatch> Matches);
public sealed record ResourceContentUpdateCommand(ResourceContentOnlineQuery File, ResourceReference Source, string VersionId);
public sealed record ResourceContentUpdateBatchCommand(IReadOnlyList<ResourceContentUpdateCommand> Updates);
public sealed record ResourceChangelogQuery(ResourceReference Source, string VersionId, bool MirrorFirst = true);
public sealed record ResourceChangelog(string Text);
public interface IResourceChangelogSource
{
    Task<ResourceChangelog> ReadChangelogAsync(ResourceChangelogQuery query, CancellationToken token);
}
