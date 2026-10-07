using Nexa.Xsr;

namespace Nexa.Services.Accounts;

public sealed record WardrobeCatalogSite(string Id, string DisplayName, Uri BaseUri,
    Uri DocumentationUri, string Icon, bool SupportsCapes);

public enum WardrobeCatalogKind
{
    Skin,
    Cape
}

public enum WardrobeCatalogOrder
{
    Time,
    Likes
}

public sealed record WardrobeCatalogSitesQuery;
public sealed record WardrobeCatalogQuery(string SiteId = "littleskin", int Page = 1,
    WardrobeCatalogKind Kind = WardrobeCatalogKind.Skin,
    WardrobeCatalogOrder Order = WardrobeCatalogOrder.Time, string Keyword = "", bool ForceRefresh = false);
public sealed record WardrobeCatalogResolveQuery(string SiteId, long TextureId);
/// <summary>Public image preview identified by a fixed catalogue site and its texture ID; no account or caller URL.</summary>
public sealed record WardrobeCatalogPreviewQuery(string SiteId, long TextureId, WardrobeCatalogKind Kind);

/// <summary>Public catalogue metadata; model is the provider's steve, alex, or cape value.</summary>
public sealed record WardrobeCatalogItem(long TextureId, string Name, string Uploader, string Model,
    int Likes, bool IsHighDefinition, string TextureAddress, Uri DetailsUri, WardrobeCatalogKind Kind);
public sealed record WardrobeCatalogPage(string SiteId, string SiteName, string ServerVersion, int Page,
    bool HasPreviousPage, bool HasNextPage, IReadOnlyList<WardrobeCatalogItem> Items);

public static class WardrobeCatalogContract
{
    public static readonly XsrSemanticId Sites = XsrSemanticId.Parse("accounts.wardrobe.catalog.sites");
    public static readonly XsrSemanticId Read = XsrSemanticId.Parse("accounts.wardrobe.catalog.read");
    public static readonly XsrSemanticId Resolve = XsrSemanticId.Parse("accounts.wardrobe.catalog.resolve");
    public static readonly XsrSemanticId Preview = XsrSemanticId.Parse("accounts.wardrobe.catalog.preview");
}
