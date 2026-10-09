using System.Globalization;
using Nexa.Services.Resources;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class ResourcesPageController
{
    private const int ContinuousProjectBudget = 1000;
    private const int ContinuousPageBudget = 500;
    private const int ContinuousOccurrenceBudget = 16000;
    private const long ContinuousTextBudget = 8 * 1024 * 1024;
    private readonly SortedDictionary<int, RetainedCatalogPage> _retainedPages = [];
    private CancellationTokenSource _listStop = new();
    private ResourceSearchQuery? _listRequest;
    private long _listGeneration, _requestGeneration, _listLayoutVersion;
    private int _nextCatalogPage;
    private bool _listHasMore = true, _listBudgetReached, _listMediaPaused;
    private string? _listFailure;
    private XsrUiEntityId _listBudgetNotice;

    private sealed class RetainedCatalogPage(ResourceSearchQuery query, ResourceSearchResult result)
    {
        internal ResourceSearchQuery Query { get; } = query;
        internal ResourceSearchResult Result { get; set; } = result;
        internal bool Renewed { get; set; }
        internal Task<XsrResult<ResourceSearchResult>>? Renewal { get; set; }
        internal long TextBytes { get; set; }
    }

    private void InitializeContinuousList()
    {
        _listBudgetNotice = Element(_entities["ResourcesLayout"], "ResourceListBudget", XsrUiSemanticRole.Text, "已达到列表显示上限，请缩小搜索范围。");
        E(_listBudgetNotice).Height = 28;
        _shell.Tree.SetComponent(_listBudgetNotice, new XsrUiText("已达到列表显示上限，请缩小搜索范围。") { MaxLines = 1, TrimOverflow = true });
        Style(_listBudgetNotice, XsrUiColor.Transparent, Muted, 0, 11);
        _entities["ResourceListBudget"] = _listBudgetNotice;
        E(_listBudgetNotice).IsVisible = false;
    }

    private void BeginContinuousSearch()
    {
        Cancel(); PauseContinuousList(); DestroyVirtualRows();
        CancelIcons(); ReleaseIcons(); _iconPage = Page; CancelTranslations();
        _listGeneration++;
        _retainedPages.Clear(); _result = null; _nextCatalogPage = 0;
        _listHasMore = true; _listBudgetReached = false; _listMediaPaused = false; _listFailure = null;
        _filter = _filter with { Text = Draft(_search), GameVersion = Draft(_game), Loader = Draft(_loader), Page = 0, WaitForRefresh = false };
        Clear(_entities["ResourceList"], _listActions);
        _shell.Tree.GetComponent<XsrUiScroll>(_entities["ResourceList"])!.OffsetY = 0;
        _listLayoutVersion = _shell.Renderer.SceneVersion;
        if (_favoriteMode) { ShowFavorites(); return; }
        Text(_entities["ResourceList"], "正在加载资源…", 14, Muted, 54);
        RequestNextPage();
    }

    private void RequestNextPage()
    {
        if (_disposed || _favoriteMode || _shell.Stage.Navigation.Current != Page || _searching is not null
            || !_listHasMore || _listBudgetReached) return;
        if (_nextCatalogPage >= ContinuousPageBudget)
        { _listBudgetReached = true; UpdateContinuousStatus(); return; }
        _listFailure = null; _listMediaPaused = false;
        if (!_queries.TryResolve(ResourceCatalogContract.Search, out var route))
        { _listFailure = "暂时无法加载资源。请检查网络后重试。"; UpdateContinuousStatus(); return; }
        _listRequest = _filter with { Page = _nextCatalogPage, WaitForRefresh = false };
        _requestGeneration = _listGeneration;
        _searching = _queries.QueryAsync<ResourceSearchQuery, ResourceSearchResult>(route, _listRequest,
            cancellationToken: _listStop.Token).AsTask();
        Wake(_searching, _listStop.Token);
        UpdateContinuousStatus();
    }

    private void UpdateContinuousList()
    {
        if (_searching is { IsCompleted: true } completed)
        {
            var captured = _listRequest;
            _searching = null; _listRequest = null;
            bool matching = _requestGeneration == _listGeneration && captured is not null;
            bool success = matching && PendingQuery.Succeeded(completed) && completed.Result.Value?.Page == captured!.Page;
            _sidecarSignals?.ResourceSearchCompleted(success);
            if (success)
            {
                ResourceSearchResult page = completed.Result.Value!;
                bool empty = page.Projects.Count == 0;
                var retained = RetainPageResult(page);
                _retainedPages[captured!.Page] = new(captured, retained.Result) { TextBytes = retained.TextBytes };
                _nextCatalogPage = captured.Page + 1;
                _listHasMore = !empty && (page.HasMore ?? (_nextCatalogPage * ResourceCatalogContract.PageSize < page.Total));
                RebuildRetainedProjects();
                if (_nextCatalogPage >= ContinuousPageBudget && _listHasMore) _listBudgetReached = true;
                _listFailure = null;
                ShowResults();
                _listLayoutVersion = _shell.Renderer.SceneVersion;
            }
            else if (matching)
            {
                _listFailure = "暂时无法加载资源。请检查网络后重试。";
                if (_result is null) ShowFailure(_entities["ResourceList"], _listFailure, RequestNextPage, _listActions);
            }
            UpdateContinuousStatus();
        }
        foreach (var page in _retainedPages.Values)
        {
            if (page.Renewal is not { IsCompleted: true } renewal) continue;
            page.Renewal = null;
            if (!PendingQuery.Succeeded(renewal) || renewal.Result.Value?.Page != page.Query.Page)
            { UpdateContinuousStatus(); continue; }
            var refreshed = renewal.Result.Value!;
            if (page.Query.Page == _nextCatalogPage - 1)
                _listHasMore = refreshed.Projects.Count > 0 && (refreshed.HasMore ?? (_nextCatalogPage * ResourceCatalogContract.PageSize < refreshed.Total));
            var retained = RetainPageResult(refreshed, page);
            page.Result = retained.Result; page.TextBytes = retained.TextBytes;
            RebuildRetainedProjects(); ShowResults();
            _listLayoutVersion = _shell.Renderer.SceneVersion;
            UpdateContinuousStatus();
        }
        RenewRetainedPages();
        RefreshVirtualWindow();
        if (_searching is not null || _favoriteMode || _listFailure is not null || !_listHasMore || _listBudgetReached) return;
        if (_result is null) { RequestNextPage(); return; }
        // Never use the loading row or pre-append geometry to admit another page.
        if (_shell.Renderer.SceneVersion <= _listLayoutVersion
            || !_shell.Renderer.TryGetScrollSnapshot(_entities["ResourceList"], out var scroll)) return;
        double threshold = Math.Max(160, scroll.ViewportHeight / 2);
        if (scroll.MaximumOffsetY - scroll.OffsetY <= threshold) RequestNextPage();
    }

    private void RenewRetainedPages()
    {
        if (!_queries.TryResolve(ResourceCatalogContract.Search, out var route)) return;
        bool admitted = false;
        foreach (var page in _retainedPages.Values)
        {
            if (page.Renewed || !page.Result.IsStale) continue;
            page.Renewed = true; admitted = true;
            page.Renewal = _queries.QueryAsync<ResourceSearchQuery, ResourceSearchResult>(route,
                page.Query with { WaitForRefresh = true }, cancellationToken: _listStop.Token).AsTask();
            Wake(page.Renewal, _listStop.Token);
        }
        if (admitted) UpdateContinuousStatus();
    }

    private (ResourceSearchResult Result, long TextBytes) RetainPageResult(ResourceSearchResult incoming, RetainedCatalogPage? replacing = null)
    {
        int occurrences = _retainedPages.Values.Sum(page => page.Result.Projects.Count) - (replacing?.Result.Projects.Count ?? 0);
        long bytes = _retainedPages.Values.Sum(page => page.TextBytes) - (replacing?.TextBytes ?? 0);
        var index = new CatalogFactIndex();
        foreach (var page in _retainedPages.Values.Where(page => page != replacing))
            foreach (var fact in page.Result.Projects) index.Admit(fact);
        var retained = new List<ResourceProject>();
        long retainedBytes = 0;
        foreach (var project in incoming.Projects)
        {
            long estimate = EstimateProjectText(project);
            if (occurrences >= ContinuousOccurrenceBudget || bytes + estimate > ContinuousTextBudget)
            { _listBudgetReached = true; break; }
            if (!index.Admit(project)) { _listBudgetReached = true; continue; }
            retained.Add(project); occurrences++; bytes += estimate; retainedBytes += estimate;
        }
        return (incoming with { Projects = retained.ToArray() }, retainedBytes);
    }

    private static long EstimateProjectText(ResourceProject project)
    {
        long characters = (long)project.Id.Length + project.Title.Length + project.Description.Length + project.Author.Length
            + project.Website.Length + project.Slug.Length + (project.IconUrl?.Length ?? 0)
            + (project.ChineseName?.Length ?? 0) + (project.ChineseDescription?.Length ?? 0);
        foreach (var source in project.Sources) characters += source.ProjectId.Length;
        return characters * sizeof(char);
    }

    private sealed class RetainedProjectFact(ResourceProject project, int order)
    {
        internal ResourceProject Project { get; set; } = project;
        internal int Order { get; } = order;
        internal bool Active { get; set; } = true;
    }

    private sealed class CatalogFactIndex
    {
        private readonly List<RetainedProjectFact> _facts = [];
        private readonly Dictionary<ResourceReference, RetainedProjectFact> _sources = [];
        private readonly Dictionary<string, RetainedProjectFact> _unqualified = new(StringComparer.Ordinal);
        private int _active;
        internal ResourceProject[] Projects() => _facts.Where(fact => fact.Active).Select(fact => fact.Project).ToArray();

        internal bool Admit(ResourceProject project)
        {
            var duplicates = project.Sources.Select(source => _sources.GetValueOrDefault(source))
                .OfType<RetainedProjectFact>().Distinct().OrderBy(fact => fact.Order).ToArray();
            RetainedProjectFact? target = duplicates.FirstOrDefault();
            if (project.Sources.Count == 0) _unqualified.TryGetValue(project.Id, out target);
            if (target is null)
            {
                if (_active >= ContinuousProjectBudget) return false;
                target = new(project, _facts.Count); _facts.Add(target); _active++;
                if (project.Sources.Count == 0) _unqualified[project.Id] = target;
            }
            else
            {
                var joined = target.Project.Sources.Concat(project.Sources)
                    .Concat(duplicates.Skip(1).SelectMany(fact => fact.Project.Sources)).Distinct().ToArray();
                target.Project = target.Project with { Sources = joined };
                foreach (var duplicate in duplicates.Skip(1)) { duplicate.Active = false; _active--; }
            }
            foreach (var source in target.Project.Sources) _sources[source] = target;
            return true;
        }
    }

    private void RebuildRetainedProjects()
    {
        var index = new CatalogFactIndex();
        // Projection never overwrites raw page membership. A bridge joins the exact-source
        // connected component, so subsequent renewal can still reconstruct unrelated pages.
        foreach (var page in _retainedPages.Values)
            foreach (var project in page.Result.Projects)
                if (!index.Admit(project)) _listBudgetReached = true;
        var projects = index.Projects();
        var last = _retainedPages.Values.LastOrDefault()?.Result;
        string? notice = string.Join(" · ", _retainedPages.Values.Select(page => page.Result.Notice).Where(text => !string.IsNullOrWhiteSpace(text)).Distinct());
        _result = new(projects, last?.Total ?? projects.Length, Math.Max(0, _nextCatalogPage - 1))
        { HasMore = _listHasMore, IsStale = _retainedPages.Values.Any(page => page.Result.IsStale), Notice = notice };
        if (_listHasMore && (projects.Length >= ContinuousProjectBudget
            || _retainedPages.Values.Sum(page => page.Result.Projects.Count) >= ContinuousOccurrenceBudget
            || _retainedPages.Values.Sum(page => page.TextBytes) >= ContinuousTextBudget)) _listBudgetReached = true;
    }

    private void UpdateContinuousStatus()
    {
        bool refreshing = _retainedPages.Values.Any(page => page.Renewal is not null);
        string status = _searching is not null ? _result is null ? "正在搜索双源目录…" : "正在加载更多资源…"
            : _listFailure ?? (_result is null ? "加载失败" : string.Format(CultureInfo.InvariantCulture,
                _shell.Renderer.LocalizeText("已加载 {0} 项"), _result.Projects.Count));
        if (_result is not null && _searching is null && _listFailure is null)
        {
            if (refreshing) status += " · " + _shell.Renderer.LocalizeText("正在显示缓存资料，后台正在刷新…");
            else if (_result.IsStale) status += " · " + _shell.Renderer.LocalizeText("正在显示缓存资料。");
            if (!string.IsNullOrWhiteSpace(_result.Notice)) status += " · " + _shell.Renderer.LocalizeText(_result.Notice);
        }
        // Localize finite status fragments before appending provider notices/counts.
        bool literal = _result is not null && _searching is null && _listFailure is null;
        _shell.Tree.SetComponent(_status, new XsrUiText(status) { Localize = !literal, MaxLines = 1, TrimOverflow = true });
        _shell.Tree.GetComponent<XsrUiInput>(_previous)!.Enabled = _result?.Projects.Count > 0;
        _shell.Tree.GetComponent<XsrUiInput>(_next)!.Enabled = _searching is null && !_favoriteMode && _listHasMore && !_listBudgetReached;
        string label = _listFailure is null ? "加载更多" : "重试加载";
        _shell.Tree.GetComponent<XsrUiText>(_next)!.Content = label;
        _shell.Tree.GetComponent<XsrUiSemantic>(_next)!.Label = label;
        E(_listBudgetNotice).IsVisible = _listBudgetReached;
        _shell.Tree.MarkDirty(_status, XsrUiDirtyKinds.Paint);
        _shell.Tree.MarkDirty(_next, XsrUiDirtyKinds.Paint);
        _shell.Tree.MarkDirty(_listBudgetNotice, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private void ReturnToListTop()
    {
        _shell.Tree.GetComponent<XsrUiScroll>(_entities["ResourceList"])!.OffsetY = 0;
        RefreshVirtualWindow();
        _shell.Tree.MarkDirty(_entities["ResourceList"], XsrUiDirtyKinds.Layout);
    }

    private void PauseContinuousList()
    {
        _listStop.Cancel(); _listStop.Dispose(); _listStop = new();
        _searching = null; _listRequest = null;
        foreach (var page in _retainedPages.Values) page.Renewal = null;
        DeactivateListMedia();
        if (!_disposed && _listBudgetNotice.IsAssigned) UpdateContinuousStatus();
    }

    private void DisposeContinuousList()
    {
        PauseContinuousList(); _listStop.Dispose();
        // The page tree owns attached views; callers may still inspect the released image
        // placeholders until the shell retires that tree. Detached virtual views have no owner.
        foreach (var row in _catalogRows.Values.Where(row => !row.Attached).ToArray()) RemoveCatalogRow(row);
        _retainedPages.Clear();
    }
}
