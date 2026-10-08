using Nexa.Services.Resources;

namespace Nexa.Desktop.Ui;

internal sealed partial class ResourcesPageController
{
    private bool _catalogSearchRenewed, _catalogDetailRenewed;
    private void ResetCatalogRenewal() { _catalogSearchRenewed = _catalogDetailRenewed = false; }
    private void RenewCatalogSearchIfStale()
    {
        if (_result is not { IsStale: true } || _catalogSearchRenewed || _favoriteMode
            || !_queries.TryResolve(ResourceCatalogContract.Search, out var route)) return;
        _catalogSearchRenewed = true;
        // Keep the current rows visible while joining the service's bounded background flight.
        _searching = _queries.QueryAsync<ResourceSearchQuery, ResourceSearchResult>(route, _filter with { WaitForRefresh = true }, cancellationToken: _stop.Token).AsTask();
        Wake(_searching);
    }
    private void RenewCatalogDetailIfStale()
    {
        if (_detail is not { IsStale: true } || _catalogDetailRenewed || _detailId is not { } id
            || !_queries.TryResolve(ResourceCatalogContract.Detail, out var route)) return;
        _catalogDetailRenewed = true;
        string loader = _filter.Kind == ResourceKind.DataPack ? "datapack" : _filter.Kind == ResourceKind.ResourcePack ? "" : _filter.Loader;
        _reading = _queries.QueryAsync<ResourceDetailQuery, ResourceDetail>(route,
            new(id, _filter.GameVersion, loader) { Sources = _detailProject?.Sources ?? [], MirrorFirst = _filter.MirrorFirst, WaitForRefresh = true }, cancellationToken: _stop.Token).AsTask();
        Wake(_reading);
    }
}
