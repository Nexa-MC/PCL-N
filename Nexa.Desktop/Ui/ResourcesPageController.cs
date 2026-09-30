using System.Collections.Concurrent;
using Nexa.Pxml;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Resources;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

internal static class ResourcesPresentationState
{
    internal static readonly XsrSemanticId Wake = XsrSemanticId.Parse("ui.resources.wake");
    internal static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<long>(Wake, "Nexa.Desktop.Resources");
}

/// <summary>Presentation-only catalog. Request replacement cancels and forgets the old query.</summary>
internal sealed class ResourcesPageController : IDisposable
{
    private static readonly XsrSemanticId Action = XsrSemanticId.Parse("ui.resources.action");
    private static readonly XsrUiColor Ink = new(43, 51, 64), Muted = new(113, 124, 140), Blue = new(11, 91, 203), Tint = new(241, 245, 250), White = new(255, 255, 255);
    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly XsrQueryRouter _queries;
    private readonly XsrStateStore _store;
    private readonly Action<Uri> _open;
    private readonly Dictionary<XsrUiEntityId, Action> _actions = [];
    private readonly ConcurrentQueue<XsrUiEntityId> _pending = new();
    private readonly Dictionary<string, XsrUiEntityId> _entities = [];
    private readonly List<XsrUiEntityId> _listActions = [];
    private readonly List<XsrUiEntityId> _detailActions = [];
    private readonly XsrUiEntityId _search, _game, _loader, _status, _previous, _next, _detailBody;
    private CancellationTokenSource _stop = new();
    private CancellationTokenSource _contextStop = new();
    private CancellationTokenSource _iconStop = new();
    private XsrCommandRouter? _commands;
    private Func<Task<string?>>? _pickDownload;
    private DesktopFeedbackService? _feedback;
    private readonly CancellationTokenSource _downloadsStop = new();
    private CancellationTokenSource _planStop = new();
    private Task<XsrResult<ResourceInstallPlan>>? _planning;
    private ResourceModInstallCommand? _installDraft;
    private bool _optionalVisible;
    private XsrUiEntityId _optionalInstall;
    private bool _installReturnDetail;
    private readonly HashSet<ResourceReference> _optionalSelection = [];
    private bool _picking, _favoriteMode, _hideInstalled, _hideLibraries;
    private Task<XsrResult<ResourceFavoritesSnapshot>>? _favoritesReading;
    private ResourceFavoritesSnapshot _favorites = new([]);
    private Task<XsrResult<ResourceInstanceContext>>? _contextReading;
    private ResourceInstanceQuery? _contextRequest;
    private ResourceInstanceContext? _context;
    private readonly ConcurrentQueue<Action> _presentation = new();
    private readonly List<(XsrUiEntityId Entity, Task<XsrResult<ResourceTranslation>> Read)> _translations = [];
    private ResourceProject? _detailProject;
    private Task<XsrResult<ResourceSearchResult>>? _searching;
    private Task<XsrResult<ResourceDetail>>? _reading;
    private readonly List<(XsrUiEntityId Entity, Task<XsrResult<ResourceIconResult>> Read)> _icons = [];
    private Task<XsrResult<MinecraftInstallEditSnapshot>>? _instanceReading;
    private MinecraftInstallEditQuery? _instanceRequest;
    private XsrQueryRouter? _instanceQueries;
    private Func<MinecraftInstallEditQuery?>? _selectedInstance;
    private ResourceSearchQuery _filter = new();
    private ResourceSearchResult? _result;
    private ResourceDetail? _detail;
    private string? _detailId;
    private int _detailPage;
    private bool _started, _disposed, _visible;
    private long _wake;
    private XsrUiThickness _previousPadding;
    internal XsrUiEntityId Page { get; }
    internal XsrUiEntityId DetailPage { get; }

    internal ResourcesPageController(XsrUiShell shell, DesktopUiIntentSink intents, XsrQueryRouter queries,
        XsrStateStore store, Action<Uri> open)
    {
        _shell = shell; _intents = intents; _queries = queries; _store = store; _open = open;
        using var stream = typeof(ResourcesPageController).Assembly.GetManifestResourceStream("Nexa.Desktop.Ui.ResourcesPage.pxml")!;
        using var reader = new StreamReader(stream);
        var host = shell.Tree.Create("resources-loader");
        Page = PxmlUiLoader.Load(PxmlCompiler.Compile(PxmlParser.Parse(reader.ReadToEnd())), shell.Tree, store, host);
        shell.Tree.Detach(Page); shell.Tree.Destroy(host);
        shell.Tree.Walk(Page, entity => { _entities[shell.Tree.Name(entity)] = entity; return true; });
        shell.Tree.SetComponent(_entities["ResourceList"], new XsrUiScrollGesture());
        Segment(_entities["ResourceCategories"], "ResourceCategory", ["收藏", "模组", "整合包", "资源包", "光影", "数据包"], index =>
        { _favoriteMode = index == 0; if (!_favoriteMode) _filter = _filter with { Kind = (ResourceKind)(index - 1) }; Search(0); }, 62, initial: 1);
        var header = _entities["ResourceCategories"];
        E(Element(header, "ResourceHeaderSpace")).Weight = 1;
        Segment(header, "ResourceOrder", ["相关", "热门", "更新"], index =>
        { _filter = _filter with { Order = (ResourceOrder)index }; Search(0); }, 54);
        var toolbar = _entities["ResourceToolbar"];
        _search = Input(toolbar, "ResourceSearch", "搜索资源", 0); E(_search).Weight = 1;
        _game = Input(toolbar, "ResourceGame", "游戏版本", 104);
        _loader = Input(toolbar, "ResourceLoader", "加载器", 100);
        Button(toolbar, "ResourceSearchButton", "搜索", 56, () => Search(0));
        Button(toolbar, "ResourceCurrentInstance", "当前版本", 76, UseCurrentInstance);
        Button(toolbar, "ResourceReset", "重置", 48, () =>
        {
            foreach (var input in new[] { _search, _game, _loader }) _shell.Renderer.SetTextInputValue(input, "");
            Search(0);
        });
        var pagination = _entities["ResourcePagination"];
        _status = Text(pagination, "Modrinth", 12, Muted, 28); E(_status).Weight = 1;
        Segment(pagination, "ResourceNetwork", ["镜像优先", "官方优先"], index => { _filter = _filter with { MirrorFirst = index == 0 }; Search(0); }, 66);
        E(_entities["ResourceNetwork.0"]).Height = 28; E(_entities["ResourceNetwork.1"]).Height = 28;
        Check(pagination, "ResourceHideInstalled", "隐藏已有", () => _hideInstalled, value => { _hideInstalled = value; ReadContext(); if (_result is not null) ShowResults(); });
        Check(pagination, "ResourceHideLibraries", "隐藏前置", () => _hideLibraries, value => { _hideLibraries = value; if (_result is not null) ShowResults(); });
        _previous = Button(pagination, "ResourcePrevious", "上一页", 68, () => Search(Math.Max(0, _filter.Page - 1)));
        _next = Button(pagination, "ResourceNext", "下一页", 68, () => Search(_filter.Page + 1));
        E(_previous).Height = 28; E(_next).Height = 28;
        DetailPage = Element(default, "ResourceDetailPage", XsrUiSemanticRole.Page, "资源详情");
        _detailBody = Stack(DetailPage, "ResourceDetailBody"); E(_detailBody).Weight = 1; E(_detailBody).Padding = new(24, 20, 24, 24);
        shell.Tree.SetComponent(_detailBody, new XsrUiScroll { ShowsVerticalIndicator = true });
        shell.Tree.SetComponent(_detailBody, new XsrUiScrollGesture());
        _intents.IntentEmitted += OnIntent; shell.Renderer.FramePreparing += OnFrame;
    }

    private void OnIntent(object? sender, DesktopUiIntentEventArgs args)
    {
        if (args.Intent.Command == Action && (_shell.Stage.Navigation.Current == Page || _shell.Stage.Navigation.Current == DetailPage)) _pending.Enqueue(args.Intent.Source);
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        if (_disposed) return;
        bool visible = _shell.Stage.Navigation.Current == Page || _shell.Stage.Navigation.Current == DetailPage;
        if (visible != _visible)
        {
            var content = E(_shell.Content);
            if (visible) { _previousPadding = content.Padding; content.Padding = default; }
            else
            {
                content.Padding = _previousPadding;
                if (_searching is not null || _icons.Count > 0) _started = false;
                Cancel(); CancelIcons(); _planStop.Cancel(); _planning = null; _installDraft = null; _contextStop.Cancel(); _contextRequest = null; _contextReading = null;
            }
            _visible = visible; _shell.Tree.MarkDirty(_shell.Content, XsrUiDirtyKinds.Layout);
        }
        if (!visible) return;
        if (_optionalVisible && _shell.Stage.Navigation.Current != DetailPage)
        { _optionalVisible = false; _planStop.Cancel(); _planning = null; _installDraft = null; }
        while (_presentation.TryDequeue(out var publish)) publish();
        if (_hideInstalled) ReadContext();
        if (_contextReading is { IsCompleted: true } contextRead)
        {
            _contextReading = null;
            if (_contextRequest == CurrentInstance() && contextRead.IsCompletedSuccessfully && contextRead.Result.IsSuccess)
            { _context = contextRead.Result.Value; if (_result is not null && _shell.Stage.Navigation.Current == Page) ShowResults(); }
        }
        if (_favoritesReading is { IsCompleted: true } favoritesRead)
        {
            _favoritesReading = null;
            if (favoritesRead.IsCompletedSuccessfully && favoritesRead.Result.IsSuccess)
            {
                _favorites = favoritesRead.Result.Value!;
                if (_favoriteMode && _shell.Stage.Navigation.Current == Page) ShowFavorites();
                else if (_shell.Stage.Navigation.Current == DetailPage && _detail is not null) UpdateFavoriteButton();
            }
            else _feedback?.Error("无法读取或保存收藏夹，请重试。");
        }
        if (_planning is { IsCompleted: true } planned)
        {
            _planning = null;
            if (planned.IsCompletedSuccessfully && planned.Result.IsSuccess && _installDraft is { } draft && draft.Instance == CurrentInstance())
            {
                var plan = planned.Result.Value!;
                if (plan.Optional.Count == 0) { _installDraft = null; _ = RunInstallAsync(draft); }
                else
                {
                    if (_shell.Stage.Navigation.Current != DetailPage) _shell.Stage.Navigation.Push(DetailPage);
                    ShowOptionalDependencies(plan);
                }
            }
            else _feedback?.Error(planned.IsCompletedSuccessfully ? planned.Result.Error?.Message ?? "无法解析依赖，请重试。" : "无法解析依赖，请重试。");
        }
        for (int i = _translations.Count - 1; i >= 0; i--)
        {
            var (entity, read) = _translations[i];
            if (!read.IsCompleted) continue;
            _translations.RemoveAt(i);
            if (read.IsCompletedSuccessfully && read.Result.IsSuccess && read.Result.Value?.Description is { } description)
            {
                var text = _shell.Tree.GetComponent<XsrUiText>(entity);
                if (text is null) continue;
                _shell.Tree.SetComponent(entity, new XsrUiText(description) { MaxLines = text.MaxLines, TrimOverflow = true });
                _shell.Tree.SetComponent(entity, new XsrUiSemantic(XsrUiSemanticRole.Text, description));
                _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
            }
        }
        for (int i = _icons.Count - 1; i >= 0; i--)
        {
            var (entity, read) = _icons[i];
            if (!read.IsCompleted) continue;
            _icons.RemoveAt(i);
            if (read.IsCompletedSuccessfully && read.Result.IsSuccess && read.Result.Value?.Image is { } image)
            {
                _shell.Tree.GetComponent<XsrUiImage>(entity)!.Raster = new(image,
                    [new(new(0, 0, image.Width, image.Height), new(0, 0, 1, 1))])
                { FitToBounds = true };
                _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
            }
        }
        _shell.Tree.GetComponent<XsrUiInput>(_entities["ResourceCurrentInstance"])!.Enabled = _instanceReading is null && _selectedInstance?.Invoke() is not null;
        if (!_started) { _started = true; ReadFavorites(); Search(_filter.Page); }
        while (_pending.TryDequeue(out var source)) if (_actions.TryGetValue(source, out var action)) action();
        if (_instanceReading is { IsCompleted: true } instanceReading)
        {
            _instanceReading = null;
            if (_instanceRequest != _selectedInstance?.Invoke())
                _shell.Tree.SetComponent(_status, new XsrUiText("当前选择已改变，请重新选择筛选。"));
            else if (instanceReading.IsCompletedSuccessfully && instanceReading.Result.IsSuccess)
            {
                var instance = instanceReading.Result.Value!;
                _shell.Renderer.SetTextInputValue(_game, instance.GameVersion);
                string loader = instance.Selection.Where(item => item.Loader is InstallLoader.Fabric or InstallLoader.Quilt or InstallLoader.Forge or InstallLoader.NeoForge).Select(item => item.Loader.ToString().ToLowerInvariant()).FirstOrDefault() ?? "";
                _shell.Renderer.SetTextInputValue(_loader, loader is "fabric" or "quilt" or "forge" or "neoforge" ? loader : "");
                Search(0);
            }
            else _shell.Tree.SetComponent(_status, new XsrUiText("无法读取当前版本，请手动填写筛选条件。"));
        }
        if (_shell.Stage.Navigation.Current == Page && _reading is not null) { Cancel(); _detailId = null; }
        if (_searching is { IsCompleted: true } searching)
        {
            _searching = null;
            if (searching.IsCompletedSuccessfully && searching.Result.IsSuccess)
            { _result = searching.Result.Value!; ShowResults(); }
            else ShowFailure(_entities["ResourceList"], "暂时无法加载资源。请检查网络后重试。", () => Search(_filter.Page), _listActions);
            UpdatePagination();
        }
        if (_reading is { IsCompleted: true } reading)
        {
            _reading = null;
            if (reading.IsCompletedSuccessfully && reading.Result.IsSuccess) { _detail = reading.Result.Value!; ShowDetail(); }
            else ShowFailure(_detailBody, "暂时无法加载详情。请重试。", () => ReadDetail(_detailId!), _detailActions);
        }
    }

    private string Draft(XsrUiEntityId entity) => _shell.Tree.GetComponent<XsrUiTextInput>(entity)!.ReadDraft().Trim();
    private void Search(int page)
    {
        Cancel(); CancelIcons(); _result = null;
        _filter = _filter with { Text = Draft(_search), GameVersion = Draft(_game), Loader = Draft(_loader), Page = page };
        Clear(_entities["ResourceList"], _listActions);
        if (_favoriteMode) { ShowFavorites(); return; }
        Text(_entities["ResourceList"], "正在加载资源…", 14, Muted, 54);
        _shell.Tree.GetComponent<XsrUiScroll>(_entities["ResourceList"])!.OffsetY = 0;
        if (_queries.TryResolve(ResourceCatalogContract.Search, out var route))
        {
            _searching = _queries.QueryAsync<ResourceSearchQuery, ResourceSearchResult>(route, _filter, cancellationToken: _stop.Token).AsTask();
            Wake(_searching);
        }
        UpdatePagination();
    }

    private void ShowResults()
    {
        var list = _entities["ResourceList"]; Clear(list, _listActions);
        if (_result!.Projects.Count == 0) { Text(list, "没有找到匹配的资源。试试其他关键词或筛选条件。", 14, Muted, 64); return; }
        int displayed = 0;
        foreach (var project in _result.Projects.Where(p => !_hideLibraries || p.Kind != ResourceKind.Mod || !p.IsLibrary)
            .Where(p => !_hideInstalled || p.Kind != ResourceKind.Mod || _context is null || !p.Sources.Any(reference => _context.Installed.Any(f => f.Source == reference))))
        {
            displayed++;
            var row = Card(list, "ResourceProject." + project.Id);
            var icon = Element(row, "ResourceProjectIcon"); E(icon).Width = 48; E(icon).Height = 48; E(icon).VerticalAlignment = XsrUiAlignment.Center;
            Style(icon, Tint, Muted, 12);
            _shell.Tree.SetComponent(icon, new XsrUiImage(project.Kind switch
            {
                ResourceKind.Mod => "lucide/blocks",
                ResourceKind.Shader => "nexa/content-shader",
                _ => "nexa/content-package"
            }));
            if (project.IconUrl is { } url && _queries.TryResolve(ResourceCatalogContract.Icon, out var iconRoute))
            {
                var read = _queries.QueryAsync<ResourceIconQuery, ResourceIconResult>(iconRoute, new(url), cancellationToken: _iconStop.Token).AsTask();
                _icons.Add((icon, read)); Wake(read);
            }
            var copy = Stack(row, "ResourceProjectCopy"); E(copy).Weight = 1;
            Text(copy, project.DisplayName, 15, Ink, 22, 600);
            Translate(Text(copy, project.DisplayDescription, 12, Muted, 20), project);
            Text(copy, $"{project.SourceLabel}  ·  {project.Author}  ·  {FormatDownloads(project.Downloads)} 次下载", 11, Muted, 18);
            _listActions.Add(IconButton(row, "ResourceDetails." + project.Id, "详情", "lucide/info", () =>
            {
                _detailProject = project; _detailPage = 0; _shell.Stage.Navigation.Push(DetailPage); ReadDetail(project.Id);
            }));
            _listActions.Add(IconButton(row, "ResourceQuickDownload." + project.Id, "下载", "lucide/download", () => _ = DownloadProjectAsync(project)));
        }
        if (displayed == 0) Text(list, "当前筛选下没有资源。", 14, Muted, 54);
    }

    private void ReadDetail(string id)
    {
        Cancel(); _detailId = id; _detail = null;
        Clear(_detailBody, _detailActions); Text(_detailBody, "正在加载详情…", 14, Muted, 54);
        string loader = _filter.Kind == ResourceKind.DataPack ? "datapack" : _filter.Kind == ResourceKind.ResourcePack ? "" : _filter.Loader;
        if (_queries.TryResolve(ResourceCatalogContract.Detail, out var route))
        {
            _reading = _queries.QueryAsync<ResourceDetailQuery, ResourceDetail>(route, new(id, _filter.GameVersion, loader) { Sources = _detailProject?.Sources ?? [], MirrorFirst = _filter.MirrorFirst }, cancellationToken: _stop.Token).AsTask();
            Wake(_reading);
        }
    }

    private void ShowDetail()
    {
        Clear(_detailBody, _detailActions);
        var detail = _detail!;
        var heading = Stack(_detailBody, "ResourceDetailHeading", true);
        ProjectIcon(heading, "ResourceDetailIcon", detail.Project, 64);
        var headingCopy = Stack(heading, "ResourceDetailHeadingCopy"); E(headingCopy).Weight = 1;
        Text(headingCopy, detail.Project.DisplayName, 25, Ink, 36, 650);
        Text(headingCopy, detail.Project.Author + " · " + detail.Project.SourceLabel, 12, Muted, 24);
        _detailActions.Add(IconButton(heading, "ResourceFavorite", IsSaved(detail.Project) ? "取消收藏" : "收藏", "lucide/star", () => ToggleFavorite(detail.Project)));
        Translate(Text(_detailBody, detail.Project.DisplayDescription, 14, Muted, 68, lines: 3), detail.Project);
        if (!string.IsNullOrEmpty(detail.Notice)) Text(_detailBody, detail.Notice, 12, Muted, 24);
        var tools = Stack(_detailBody, "ResourceDetailTools", horizontal: true);
        var info = Text(tools, $"{detail.Project.SourceLabel}  ·  {FormatDownloads(detail.Project.Downloads)} 次下载  ·  {detail.License}", 12, Muted, 34); E(info).Weight = 1;
        _detailActions.Add(Button(tools, "ResourceWebsite", "项目主页 ↗", 110, () => _open(new Uri(detail.Project.Website))));
        Text(_detailBody, $"版本  ·  {detail.Versions.Count} 个" + (_filter.GameVersion.Length > 0 ? "  ·  Minecraft " + _filter.GameVersion : ""), 16, Ink, 36, 600);
        if (detail.Versions.Count == 0) Text(_detailBody, "没有符合当前筛选条件的版本。", 14, Muted, 48);
        foreach (var version in detail.Versions.Skip(_detailPage * 20).Take(20))
        {
            var row = Card(_detailBody, "ResourceVersion." + version.Id);
            var type = Element(row, "ResourceVersionType", XsrUiSemanticRole.Image, version.Channel);
            E(type).Width = 22; E(type).Height = 22; E(type).VerticalAlignment = XsrUiAlignment.Center;
            _shell.Tree.SetComponent(type, new XsrUiImage(version.Channel == "正式版" ? "lucide/circle-check" : version.Channel == "Beta" ? "lucide/flask-conical" : "lucide/test-tube"));
            Style(type, XsrUiColor.Transparent, version.Channel == "正式版" ? new(40, 135, 90) : version.Channel == "Beta" ? Blue : new(188, 125, 42), 0);
            var copy = Stack(row, "ResourceVersionCopy"); E(copy).Weight = 1;
            Text(copy, version.Name, 15, Ink, 28, 550);
            Text(copy, $"{version.Provider}  ·  {version.Channel}  ·  {string.Join(" / ", version.Loaders)}  ·  Minecraft {string.Join(", ", version.Games)}", 12, Muted, 34, lines: 2);
            _detailActions.Add(IconButton(row, "ResourceDownload." + version.Id, version.File is null ? "前往下载" : "下载", version.File is null ? "lucide/arrow-up-right" : "lucide/download", () => { if (version.File is null) _open(new Uri(version.Website)); else _ = DownloadAsync(version); }));
        }
        if (detail.Versions.Count > 20)
        {
            var pages = Stack(_detailBody, "ResourceVersionPages", horizontal: true);
            var previous = Button(pages, "ResourceVersionPrevious", "上一页", 68, () => { _detailPage--; ShowDetail(); });
            var next = Button(pages, "ResourceVersionNext", "下一页", 68, () => { _detailPage++; ShowDetail(); });
            _detailActions.Add(previous); _detailActions.Add(next);
            _shell.Tree.GetComponent<XsrUiInput>(previous)!.Enabled = _detailPage > 0; _shell.Tree.GetComponent<XsrUiInput>(next)!.Enabled = (_detailPage + 1) * 20 < detail.Versions.Count;
            Text(pages, $"{_detailPage + 1} / {(detail.Versions.Count + 19) / 20}", 12, Muted, 34);
        }
        _shell.Tree.GetComponent<XsrUiScroll>(_detailBody)!.OffsetY = 0;
    }

    private void UpdatePagination()
    {
        _shell.Tree.SetComponent(_status, new XsrUiText(_searching is not null ? "正在搜索双源目录…" : _result is null ? "加载失败" : $"第 {_filter.Page + 1} 页 · {_result.Projects.Count} 项" + (string.IsNullOrEmpty(_result.Notice) ? "" : " · " + _result.Notice)) { MaxLines = 1, TrimOverflow = true });
        _shell.Tree.GetComponent<XsrUiInput>(_previous)!.Enabled = _searching is null && _filter.Page > 0;
        _shell.Tree.GetComponent<XsrUiInput>(_next)!.Enabled = _searching is null && _result is not null && (_result.HasMore ?? ((_filter.Page + 1) * 20 < _result.Total));
    }
    private void ShowFailure(XsrUiEntityId parent, string message, Action retry, List<XsrUiEntityId> actions)
    {
        Clear(parent, actions); Text(parent, message, 14, Muted, 54);
        actions.Add(Button(parent, "ResourceRetry", "重试", 80, retry));
    }
    internal void ConfigureInstanceFilter(XsrQueryRouter queries, Func<MinecraftInstallEditQuery?> selected)
    { _instanceQueries = queries; _selectedInstance = selected; }
    private void UseCurrentInstance()
    {
        if (_selectedInstance?.Invoke() is not { } query || _instanceQueries is null || !_instanceQueries.TryResolve(MinecraftInstallEditContract.Query, out var route)) return;
        Cancel();
        _instanceRequest = query;
        _instanceReading = _instanceQueries.QueryAsync<MinecraftInstallEditQuery, MinecraftInstallEditSnapshot>(route, query, cancellationToken: _stop.Token).AsTask();
        _shell.Tree.SetComponent(_status, new XsrUiText("正在读取当前版本…"));
        Wake(_instanceReading);
    }
    internal void ConfigureDownloads(XsrCommandRouter commands, Func<Task<string?>> picker, DesktopFeedbackService feedback)
    { _commands = commands; _pickDownload = picker; _feedback = feedback; }
    private async Task DownloadAsync(ResourceVersion version, ResourceKind? kind = null)
    {
        if (_picking || _commands is null || _pickDownload is null || !_commands.TryResolve(ResourceCatalogContract.Download, out var route)) return;
        if ((kind ?? _detailProject?.Kind ?? _filter.Kind) == ResourceKind.Mod && _commands.TryResolve(ResourceCatalogContract.InstallMod, out var installRoute))
        { await InstallModAsync(new(version.Provider, version.ProjectId), version.Id, installRoute); return; }
        _picking = true;
        try
        {
            string? folder;
            try { folder = await _pickDownload(); } finally { _picking = false; }
            if (folder is null || _disposed) return;
            var result = await _commands.Dispatch(route, new ResourceDownloadCommand(version.Provider, version.ProjectId, version.Id, folder, _filter.MirrorFirst), cancellationToken: _downloadsStop.Token).Completion.ConfigureAwait(false);
            if (!_disposed && !result.IsSuccess && result.Error?.Code != XsrRuntimeErrors.Cancelled().Code) _feedback?.Error(result.Error?.Message ?? "资源下载失败。");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { if (!_disposed) _feedback?.Error("资源下载失败：" + error.Message); }
    }
    private void Translate(XsrUiEntityId entity, ResourceProject project)
    {
        if (project.Sources.Count == 0 || !_queries.TryResolve(ResourceCatalogContract.Translate, out var route)) return;
        var source = project.Sources[0];
        var read = _queries.QueryAsync<ResourceTranslationQuery, ResourceTranslation>(route, new(source, project.Description), cancellationToken: _iconStop.Token).AsTask();
        _translations.Add((entity, read)); Wake(read);
    }
    private ResourceInstanceQuery? CurrentInstance() => _selectedInstance?.Invoke() is { } selected ? new(selected.RootDirectory, selected.InstanceId, _filter.MirrorFirst) : null;
    private void ReadContext()
    {
        var query = CurrentInstance();
        if (query == _contextRequest) return;
        _contextStop.Cancel(); _contextStop.Dispose(); _contextStop = new();
        _context = null; _contextRequest = query; _contextReading = null;
        if (query is not null && _queries.TryResolve(ResourceCatalogContract.Instance, out var route))
        { _contextReading = _queries.QueryAsync<ResourceInstanceQuery, ResourceInstanceContext>(route, query, cancellationToken: _contextStop.Token).AsTask(); Wake(_contextReading); }
    }
    private void ReadFavorites()
    {
        if (_queries.TryResolve(ResourceCatalogContract.Favorites, out var route))
        { _favoritesReading = _queries.QueryAsync<ResourceFavoritesQuery, ResourceFavoritesSnapshot>(route, new(), cancellationToken: _downloadsStop.Token).AsTask(); Wake(_favoritesReading); }
    }
    private void ShowFavorites()
    {
        string text = Draft(_search);
        var matches = _favorites.Projects.Where(p => text.Length == 0 || p.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase) || p.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || p.DisplayDescription.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        _result = new(matches.Skip(_filter.Page * 20).Take(20).ToArray(), matches.Length, _filter.Page);
        ShowResults(); UpdatePagination();
    }
    private bool IsSaved(ResourceProject project) => _favorites.Projects.Any(p => p.Sources.Intersect(project.Sources).Any());
    private void UpdateFavoriteButton()
    {
        if (_detail is null || !_entities.TryGetValue("ResourceFavorite", out var button)) return;
        string label = IsSaved(_detail.Project) ? "取消收藏" : "收藏";
        _shell.Tree.SetComponent(button, new XsrUiText(label));
        _shell.Tree.SetComponent(button, new XsrUiSemantic(XsrUiSemanticRole.Button, label));
        _shell.Tree.GetComponent<XsrUiVisualStyle>(button)!.Foreground = IsSaved(_detail.Project) ? new(188, 125, 42) : Blue;
        _shell.Tree.MarkDirty(button, XsrUiDirtyKinds.Paint);
    }
    private void ToggleFavorite(ResourceProject project)
    {
        if (_favoritesReading is not null || _commands is null || !_commands.TryResolve(ResourceCatalogContract.Favorite, out var route)) return;
        project = project with { Kind = _detailProject?.Kind ?? project.Kind };
        _favoritesReading = SaveFavoriteAsync(route, new(project, !IsSaved(project))); Wake(_favoritesReading);
    }
    private async Task<XsrResult<ResourceFavoritesSnapshot>> SaveFavoriteAsync(XsrCommandId route, ResourceFavoriteCommand command)
    {
        var result = await _commands!.Dispatch(route, command, cancellationToken: _downloadsStop.Token).Completion.ConfigureAwait(false);
        if (!result.IsSuccess) return XsrResult.Failure<ResourceFavoritesSnapshot>(result.Error!);
        if (!_queries.TryResolve(ResourceCatalogContract.Favorites, out var queryRoute)) throw new InvalidOperationException("Favorites route missing.");
        return await _queries.QueryAsync<ResourceFavoritesQuery, ResourceFavoritesSnapshot>(queryRoute, new(), cancellationToken: _downloadsStop.Token).ConfigureAwait(false);
    }
    private async Task DownloadProjectAsync(ResourceProject project)
    {
        try
        {
            if (_commands is null) return;
            var reference = project.Sources.Count > 0 ? project.Sources[0] : new(ResourceProvider.Modrinth, project.Id);
            if (project.Kind == ResourceKind.Mod && _commands.TryResolve(ResourceCatalogContract.InstallMod, out var installRoute))
            { await InstallModAsync(reference, null, installRoute); return; }
            if (!_queries.TryResolve(ResourceCatalogContract.Detail, out var route)) return;
            var result = await _queries.QueryAsync<ResourceDetailQuery, ResourceDetail>(route, new(project.Id, _filter.GameVersion, project.Kind == ResourceKind.DataPack ? "datapack" : project.Kind == ResourceKind.ResourcePack ? "" : _filter.Loader) { Sources = project.Sources, MirrorFirst = _filter.MirrorFirst }, cancellationToken: _downloadsStop.Token).ConfigureAwait(false);
            var version = result.Value?.Versions.FirstOrDefault(v => v.File is not null);
            if (!result.IsSuccess || version is null) { _feedback?.Error("没有符合筛选条件的可下载版本，请打开详情选择。"); return; }
            await DownloadAsync(version, project.Kind);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is not OutOfMemoryException and not AccessViolationException) { if (!_disposed) _feedback?.Error(e.Message); }
    }
    private Task InstallModAsync(ResourceReference source, string? version, XsrCommandId route)
    {
        var instance = CurrentInstance();
        if (instance is null) { _feedback?.Error("请先在主页选择要安装模组的版本。"); return Task.CompletedTask; }
        var command = new ResourceModInstallCommand(source, version, instance);
        if (!_queries.TryResolve(ResourceCatalogContract.PlanMod, out _)) return RunInstallAsync(command);
        _optionalSelection.Clear(); _installReturnDetail = _shell.Stage.Navigation.Current == DetailPage;
        StartInstallPlan(command); return Task.CompletedTask;
    }
    private void StartInstallPlan(ResourceModInstallCommand command)
    {
        Cancel();
        _planStop.Cancel(); _planStop.Dispose(); _planStop = new(); _installDraft = command;
        if (_optionalInstall.IsAssigned && _shell.Tree.IsAlive(_optionalInstall))
            _shell.Tree.GetComponent<XsrUiInput>(_optionalInstall)!.Enabled = false;
        if (!_queries.TryResolve(ResourceCatalogContract.PlanMod, out var route)) return;
        _planning = _queries.QueryAsync<ResourceModPlanQuery, ResourceInstallPlan>(route, new(command), cancellationToken: _planStop.Token).AsTask(); Wake(_planning);
    }
    private void ShowOptionalDependencies(ResourceInstallPlan plan)
    {
        _optionalVisible = true;
        Clear(_detailBody, _detailActions);
        Text(_detailBody, "选择可选依赖", 24, Ink, 38, 650);
        Text(_detailBody, $"将安装 {plan.Versions.Count} 个模组及必需依赖。" + (plan.HasCycles ? "循环依赖会一起安装。" : ""), 13, Muted, 36);
        foreach (var option in plan.Optional)
        {
            var row = Card(_detailBody, "ResourceOptional." + option.Source.ProjectId);
            var name = Text(row, option.Name, 14, Ink, 32); E(name).Weight = 1;
            _detailActions.Add(Button(row, "ResourceOptionalToggle." + option.Source.ProjectId, _optionalSelection.Contains(option.Source) ? "✓ 已选择" : "□ 选择", 86, () =>
            {
                if (!_optionalSelection.Add(option.Source)) _optionalSelection.Remove(option.Source);
                StartInstallPlan(_installDraft! with { OptionalDependencies = _optionalSelection.ToArray() });
            }));
        }
        var actions = Stack(_detailBody, "ResourceOptionalActions", true);
        _detailActions.Add(Button(actions, "ResourceOptionalCancel", "取消", 76, () => { _planStop.Cancel(); _planning = null; _installDraft = null; CloseOptionalPage(); }));
        _optionalInstall = IconButton(actions, "ResourceOptionalInstall", "安装", "lucide/download", () =>
        {
            if (_planning is not null || _installDraft is null) return;
            var command = _installDraft with { OptionalDependencies = _optionalSelection.ToArray() }; _installDraft = null;
            CloseOptionalPage(); _ = RunInstallAsync(command);
        });
        _detailActions.Add(_optionalInstall);
    }
    private void CloseOptionalPage() { _optionalVisible = false; if (_installReturnDetail && _detail is not null) ShowDetail(); else _shell.Stage.Navigation.Pop(); }
    private async Task RunInstallAsync(ResourceModInstallCommand command)
    {
        if (_commands is null || !_commands.TryResolve(ResourceCatalogContract.InstallMod, out var route)) return;
        try
        {
            var result = await _commands.Dispatch(route, command, cancellationToken: _downloadsStop.Token).Completion.ConfigureAwait(false);
            if (_disposed) return;
            if (!result.IsSuccess && result.Error?.Code != XsrRuntimeErrors.Cancelled().Code) _feedback?.Error(result.Error?.Message ?? "模组安装失败。");
            _presentation.Enqueue(() => { _contextRequest = null; _context = null; ReadContext(); }); Wake(Task.CompletedTask);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is not OutOfMemoryException and not AccessViolationException) { if (!_disposed) _feedback?.Error(e.Message); }
    }
    internal static string FormatDownloads(long value)
    {
        double divisor = value >= 1_000_000_000 ? 1_000_000_000 : value >= 1_000_000 ? 1_000_000 : value >= 1_000 ? 1_000 : 1;
        string suffix = divisor == 1_000_000_000 ? "b" : divisor == 1_000_000 ? "m" : divisor == 1_000 ? "k" : "";
        return (value / divisor).ToString(divisor == 1 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + suffix;
    }
    private void Check(XsrUiEntityId parent, string name, string label, Func<bool> read, Action<bool> write)
    {
        XsrUiEntityId button = default;
        button = Button(parent, name, "□ " + label, 84, () => { write(!read()); _shell.Tree.SetComponent(button, new XsrUiText((read() ? "✓ " : "□ ") + label)); });
        E(button).Height = 28; Style(button, XsrUiColor.Transparent, Muted, 6, 12);
    }
    private XsrUiEntityId IconButton(XsrUiEntityId parent, string name, string label, string icon, Action action)
    {
        var entity = Button(parent, name, label, 76, action);
        _shell.Tree.SetComponent(entity, new XsrUiImage(icon));
        var style = _shell.Tree.GetComponent<XsrUiVisualStyle>(entity)!;
        style.HoverExpand = true; style.Background = DesktopUiPalette.CapsuleBackground; style.Foreground = DesktopUiPalette.CapsuleForeground;
        return entity;
    }
    private XsrUiEntityId ProjectIcon(XsrUiEntityId parent, string name, ResourceProject project, double size)
    {
        var icon = Element(parent, name, XsrUiSemanticRole.Image, project.DisplayName); E(icon).Width = size; E(icon).Height = size;
        Style(icon, Tint, Muted, 14); _shell.Tree.SetComponent(icon, new XsrUiImage("lucide/blocks"));
        if (project.IconUrl is { } url && _queries.TryResolve(ResourceCatalogContract.Icon, out var route))
        { var read = _queries.QueryAsync<ResourceIconQuery, ResourceIconResult>(route, new(url), cancellationToken: _iconStop.Token).AsTask(); _icons.Add((icon, read)); Wake(read); }
        return icon;
    }
    private void Cancel() { _stop.Cancel(); _stop.Dispose(); _stop = new(); _searching = null; _reading = null; _instanceReading = null; }
    private void CancelIcons() { _translations.Clear(); _iconStop.Cancel(); _iconStop.Dispose(); _iconStop = new(); _icons.Clear(); }
    private void Wake(Task task) => _ = task.ContinueWith(_ =>
    {
        if (_disposed) return;
        try { _store.Publish(_store.Resolve(ResourcesPresentationState.Wake), Interlocked.Increment(ref _wake)); }
        catch (ObjectDisposedException) { }
    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private void Clear(XsrUiEntityId parent, List<XsrUiEntityId> actions)
    {
        foreach (var action in actions)
        {
            _actions.Remove(action);
            foreach (var key in _entities.Where(pair => pair.Value == action).Select(pair => pair.Key).ToArray()) _entities.Remove(key);
        }
        actions.Clear();
        var removed = new HashSet<XsrUiEntityId>();
        foreach (var child in _shell.Tree.Children(parent).ToArray())
        { _shell.Tree.Walk(child, entity => { removed.Add(entity); return true; }); _shell.Tree.Destroy(child); }
        _translations.RemoveAll(item => removed.Contains(item.Entity)); _icons.RemoveAll(item => removed.Contains(item.Entity));
    }
    private XsrUiElement E(XsrUiEntityId entity) => _shell.Tree.GetComponent<XsrUiElement>(entity)!;
    private XsrUiEntityId Element(XsrUiEntityId parent, string name, XsrUiSemanticRole role = XsrUiSemanticRole.None, string? label = null)
    {
        var entity = _shell.Tree.Create(name); if (parent.IsAssigned) _shell.Tree.Attach(entity, parent);
        _shell.Tree.SetComponent(entity, new XsrUiElement()); _shell.Tree.SetComponent(entity, new XsrUiSemantic(role, label)); return entity;
    }
    private XsrUiEntityId Stack(XsrUiEntityId parent, string name, bool horizontal = false)
    {
        var entity = Element(parent, name); _shell.Tree.SetComponent(entity, new XsrUiStackPanel(horizontal ? XsrUiOrientation.Horizontal : XsrUiOrientation.Vertical) { Spacing = horizontal ? 12 : 2 }); return entity;
    }
    private XsrUiEntityId Card(XsrUiEntityId parent, string name)
    {
        var surface = Stack(parent, name); Style(surface, White, Ink, 10);
        var body = Stack(surface, name + ".Body", true); E(body).Padding = new(12, 8, 12, 8); return body;
    }
    private XsrUiEntityId Text(XsrUiEntityId parent, string value, double size, XsrUiColor color, double height, double weight = 400, int lines = 1)
    {
        var entity = Element(parent, "ResourceText", XsrUiSemanticRole.Text, value); E(entity).Height = height;
        _shell.Tree.SetComponent(entity, new XsrUiText(value) { MaxLines = lines, TrimOverflow = true }); Style(entity, XsrUiColor.Transparent, color, 0, size, weight); return entity;
    }
    private XsrUiEntityId Button(XsrUiEntityId parent, string name, string label, double width, Action action)
    {
        var entity = Element(parent, name, XsrUiSemanticRole.Button, label); E(entity).Height = 34;
        _shell.Tree.SetComponent(entity, new XsrUiText(label));
        // Stable names for acceptance tests and keyboard navigation.
        _entities[name] = entity;
        E(entity).Width = width; E(entity).VerticalAlignment = XsrUiAlignment.Center;
        _shell.Tree.SetComponent(entity, new XsrUiInput { Focusable = true, Clickable = true });
        _shell.Tree.SetComponent(entity, new XsrUiCommandBinding(Action)); Style(entity, Tint, Blue, 17);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(entity)!.TextAlignment = XsrUiTextAlignment.Center;
        _actions[entity] = action; return entity;
    }
    internal XsrUiEntityId Find(string key) => _entities[key];
    private XsrUiEntityId Input(XsrUiEntityId parent, string name, string placeholder, double width)
    {
        var surface = Stack(parent, name + ".Surface"); Style(surface, Tint, Ink, 10);
        if (width > 0) E(surface).Width = width; else E(surface).Weight = 1;
        var entity = Element(surface, name, XsrUiSemanticRole.TextInput, placeholder); E(entity).Height = 34; E(entity).Padding = new(10, 0, 10, 0);
        _shell.Tree.SetComponent(entity, new XsrUiInput { Focusable = true, Clickable = true });
        _shell.Tree.SetComponent(entity, new XsrUiTextInput { Placeholder = placeholder }); Style(entity, XsrUiColor.Transparent, Ink, 0);
        return entity;
    }
    private void Segment(XsrUiEntityId parent, string name, string[] labels, Action<int> selected, double width = 84, int initial = 0)
    {
        var track = Stack(parent, name, true); _shell.Tree.GetComponent<XsrUiStackPanel>(track)!.Spacing = 0; E(track).Width = width * labels.Length;
        Style(track, Tint, Ink, 10);
        var thumb = Element(track, name + "Thumb"); E(thumb).IsVisible = false; Style(thumb, White, Ink, 8);
        _shell.Tree.SetComponent(thumb, new XsrUiTransition()); _shell.Tree.SetComponent(track, new XsrUiSegmentedTrack(thumb));
        List<XsrUiEntityId> options = [];
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var option = Button(track, name + "." + i, labels[i], width, () =>
            {
                for (int j = 0; j < options.Count; j++) _shell.Tree.GetComponent<XsrUiSelection>(options[j])!.IsSelected = j == index;
                _shell.Tree.GetComponent<XsrUiSegmentedTrack>(track)!.Selected = options[index]; selected(index);
            });
            options.Add(option); Style(option, XsrUiColor.Transparent, Ink, 8);
            _shell.Tree.GetComponent<XsrUiVisualStyle>(option)!.TextAlignment = XsrUiTextAlignment.Center;
            _shell.Tree.SetComponent(option, new XsrUiSelection { IsSelected = i == initial });
        }
        _shell.Tree.GetComponent<XsrUiSegmentedTrack>(track)!.Selected = options[initial];
    }
    private void Style(XsrUiEntityId entity, XsrUiColor background, XsrUiColor foreground, double radius, double size = 13, double weight = 400)
    {
        _shell.Tree.SetComponent(entity, new XsrUiVisualStyle
        {
            Background = background,
            Foreground = foreground,
            CornerRadius = radius,
            FontSize = size,
            FontWeight = weight,
            Surface = XsrUiSurfaceKind.Solid,
            Hover = DesktopUiPalette.CapsuleHover
        });
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }
    public void Dispose() { _disposed = true; _planStop.Cancel(); _planStop.Dispose(); _contextStop.Cancel(); _contextStop.Dispose(); _downloadsStop.Cancel(); _downloadsStop.Dispose(); _iconStop.Cancel(); _iconStop.Dispose(); _stop.Cancel(); _stop.Dispose(); _intents.IntentEmitted -= OnIntent; _shell.Renderer.FramePreparing -= OnFrame; }
}
