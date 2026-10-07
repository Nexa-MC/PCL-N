using System.Collections.Concurrent;
using System.Globalization;
using Nexa.Pxml;
using Nexa.Services.Accounts;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>Public catalogue presentation. Accounts owns catalogue, texture and provider I/O.</summary>
internal sealed class WardrobeLibraryPageController : IDisposable
{
    private static readonly XsrSemanticId CardAction = XsrSemanticId.Parse("ui.wardrobe.library.action");
    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly XsrQueryRouter _queries;
    private readonly XsrCommandRouter _commands;
    private readonly XsrStateStore _store;
    private readonly DesktopFeedbackService _feedback;
    private readonly Dictionary<string, XsrUiEntityId> _nodes = [];
    private readonly Dictionary<XsrUiEntityId, Action> _actions = [];
    private readonly ConcurrentQueue<DesktopUiIntent> _pending = new();
    private readonly List<CardVisual> _cards = [];
    private readonly List<ImageRead> _images = [];
    private CancellationTokenSource _lifetime = new(), _catalogStop = new(), _imageStop = new();
    private Task<XsrResult<IReadOnlyList<WardrobeCatalogSite>>>? _sitesReading;
    private Task<XsrResult<WardrobeCatalogPage>>? _catalogReading;
    private Task<XsrResult<AccountWardrobeSnapshot>>? _accountReading;
    private Task<XsrResult>? _writing;
    private WardrobeCatalogSite[] _sites = [];
    private WardrobeCatalogSite? _site;
    private WardrobeCatalogPage? _catalog;
    private AccountWardrobeSnapshot? _account;
    private AccountWardrobeIdentity? _lastAdmittedIdentity;
    private LaunchProfileView _accountReadProfile;
    private long _accountReadSelectionEvents, _accountReadRosterRevision;
    private WardrobeCatalogQuery _filter = new();
    private Action<Uri>? _openUrl;
    private Action? _back;
    private bool _visible, _started, _accountLoaded, _accountFailed, _disposed, _effecting;
    private volatile bool _dirty = true;
    private long _wake, _selectionEvents, _rosterEvents, _seenSelectionEvents, _seenRosterEvents;
    private string? _observedIdentity, _notice;
    private string _siteStatus = "正在读取皮肤站 API…", _emptyTitle = "没有找到皮肤", _emptyMessage = "试试其他关键词或切换分类。";
    private int _columns = 1;
    private double _railWidth, _scrollOffset = double.NaN;
    private XsrUiSize _viewport;
    private bool _siteStatusLiteral;
    internal const int MaximumVisibleCards = 64;
    private const double CardHeight = 383, RowSpacing = 12;
    private sealed class CardVisual(WardrobeCatalogItem item, XsrUiEntityId container, XsrUiEntityId preview)
    {
        internal WardrobeCatalogItem Item { get; } = item;
        internal XsrUiEntityId Container { get; } = container;
        internal XsrUiEntityId Preview { get; } = preview;
        internal XsrUiEntityId ViewButton { get; set; }
        internal WardrobePlayerView View { get; set; } = item.Kind == WardrobeCatalogKind.Cape ? WardrobePlayerView.Back : WardrobePlayerView.Isometric;
        internal AccountWardrobeResolvedTextures? Loaded { get; set; }
        internal bool Attempted { get; set; }
    }
    private sealed record ImageRead(CardVisual Card, CancellationTokenSource Stop, Task<XsrResult<AccountWardrobeResolvedTextures>> Read);

    internal WardrobeLibraryPageController(XsrUiShell shell, DesktopUiIntentSink intents,
        XsrQueryRouter queries, XsrCommandRouter commands, XsrStateStore store, DesktopFeedbackService feedback)
    {
        _shell = shell; _intents = intents; _queries = queries; _commands = commands; _store = store; _feedback = feedback;
        using var stream = typeof(WardrobeLibraryPageController).Assembly.GetManifestResourceStream("Nexa.Desktop.Ui.WardrobeLibraryPage.pxml")
            ?? throw new InvalidOperationException("Missing wardrobe library page.");
        using var reader = new StreamReader(stream);
        var host = shell.Tree.Create("wardrobe-library-loader");
        try { Page = PxmlUiLoader.Load(PxmlCompiler.Compile(PxmlParser.Parse(reader.ReadToEnd())), shell.Tree, store, host); shell.Tree.Detach(Page); }
        finally { shell.Tree.Destroy(host); }
        shell.Tree.Walk(Page, entity =>
        {
            string name = shell.Tree.Name(entity); if (name.Length > 0) _nodes[name] = entity;
            Style(entity); return true;
        });
        foreach (string name in new[] { "WardrobeLibraryRail", "WardrobeLibraryBody", "WardrobeLibraryFilters" })
            shell.Tree.SetComponent(_nodes[name], new XsrUiScrollGesture());
        shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes["WardrobeLibrarySiteName"])!.FontSize = 20;
        shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes["WardrobeLibrarySiteName"])!.FontWeight = 600;
        _intents.IntentEmitted += OnIntent;
        shell.Renderer.FramePreparing += OnFrame;
        _store.Changed += OnStateChanged;
    }

    internal XsrUiEntityId Page { get; }
    internal void ConfigureOpenUrl(Action<Uri> openUrl) => _openUrl = openUrl;
    internal void ConfigureBack(Action back) => _back = back;
    private bool IsCurrent => !_disposed && _shell.Tree.IsAlive(Page) && _shell.Stage.Navigation.Current == Page;

    private void OnIntent(object? sender, DesktopUiIntentEventArgs args)
    {
        if (IsCurrent && !_effecting && args.Intent.Command.Value.StartsWith("ui.wardrobe.library.", StringComparison.Ordinal))
            _pending.Enqueue(args.Intent);
    }
    private void OnStateChanged(XsrStateChange change)
    {
        if (change.SemanticId == AccountStateContract.SelectedKey) Interlocked.Increment(ref _selectionEvents);
        if (change.SemanticId == AccountStateContract.ProfilesKey) Interlocked.Increment(ref _rosterEvents);
    }
    private string? CurrentIdentity()
    {
        return IdentityText(CurrentProfile());
    }
    private LaunchProfileView CurrentProfile()
    {
        int index = _store.Read<int>(_store.Resolve(AccountStateContract.SelectedKey)).Value;
        return _store.ReadCollection<LaunchProfileView>(_store.Resolve(AccountStateContract.ProfilesKey)).Items.FirstOrDefault(p => p.Index == index);
    }
    private static string? IdentityText(LaunchProfileView profile) => string.IsNullOrWhiteSpace(profile.Username) ? null
        : $"{profile.Index}\n{profile.Kind}\n{profile.Uuid}\n{profile.AuthServer}\n{profile.Username}";

    private void OnFrame(object? sender, EventArgs args)
    {
        if (_disposed) return;
        if (!IsCurrent) { if (_visible) Retire(); _visible = false; return; }
        long selections = Volatile.Read(ref _selectionEvents), rosters = Volatile.Read(ref _rosterEvents);
        double offset = _shell.Tree.GetComponent<XsrUiScroll>(_nodes["WardrobeLibraryBody"])!.OffsetY;
        if (_visible && (_accountLoaded || _accountReading is not null) && !_dirty && _pending.IsEmpty && _viewport == _shell.Renderer.Viewport && _scrollOffset == offset
            && selections == _seenSelectionEvents && rosters == _seenRosterEvents
            && _sitesReading is not { IsCompleted: true } && _catalogReading is not { IsCompleted: true }
            && _accountReading is not { IsCompleted: true } && _writing is not { IsCompleted: true }
            && !_images.Any(image => image.Read.IsCompleted)
            && (_filter.Keyword.Length == 0 || !string.IsNullOrWhiteSpace(SearchDraft()))) return;
        LaunchProfileView? pendingPrincipal = _accountReading is not null ? _accountReadProfile : _writing is not null ? _account?.Profile : null;
        bool principalChanged = rosters != _seenRosterEvents && pendingPrincipal is { } original
            && !SamePrincipal(CurrentProfile(), original);
        bool contextChanged = !_visible || selections != _seenSelectionEvents || principalChanged
            || rosters != _seenRosterEvents && _accountReading is null && _writing is null;
        if (contextChanged)
        {
            Retire(); _observedIdentity = CurrentIdentity(); _visible = true;
            _seenSelectionEvents = selections; _seenRosterEvents = rosters;
        }
        UpdateResponsiveLayout();
        if (!_started) { _started = true; BeginSites(); }
        if (!_accountLoaded && _accountReading is null) BeginAccountRead();
        ConsumeReplies();
        while (_pending.TryDequeue(out var intent))
        {
            if (!IsCurrent) break;
            Handle(intent);
            if (!IsCurrent) return;
        }
        // Clearing a committed search restores the unfiltered catalogue without another click.
        if (_filter.Keyword.Length > 0 && string.IsNullOrWhiteSpace(SearchDraft()) && _writing is null)
            Search();
        ConsumeImages();
        Present();
        _scrollOffset = _shell.Tree.GetComponent<XsrUiScroll>(_nodes["WardrobeLibraryBody"])!.OffsetY;
    }

    private void BeginSites()
    {
        _dirty = true;
        if (!_queries.TryResolve(WardrobeCatalogContract.Sites, out var route))
        { SetEmpty("没有皮肤站", "尚未配置可用的皮肤站。"); return; }
        _sitesReading = _queries.QueryAsync<WardrobeCatalogSitesQuery, IReadOnlyList<WardrobeCatalogSite>>(route, new(), cancellationToken: _lifetime.Token).AsTask();
        Wake(_sitesReading, _lifetime.Token);
    }
    private void BeginAccountRead()
    {
        _dirty = true;
        if (_observedIdentity is null || !_queries.TryResolve(AccountWardrobeContract.Read, out var route)) { _accountLoaded = true; return; }
        _accountReadSelectionEvents = Volatile.Read(ref _selectionEvents);
        _accountReadProfile = CurrentProfile();
        _accountReadRosterRevision = _store.ReadCollection<LaunchProfileView>(_store.Resolve(AccountStateContract.ProfilesKey)).Revision;
        _accountReading = _queries.QueryAsync<AccountWardrobeQuery, AccountWardrobeSnapshot>(route, new(), cancellationToken: _lifetime.Token).AsTask();
        Wake(_accountReading, _lifetime.Token);
    }
    private void Reload(bool force = false)
    {
        _catalogStop.Cancel(); _catalogStop.Dispose(); _catalogStop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _catalogReading = null; _catalog = null; ClearCards(); _dirty = true;
        if (_site is null) { SetEmpty("没有皮肤站", "尚未配置可用的皮肤站。"); return; }
        _filter = _filter with { SiteId = _site.Id, ForceRefresh = force };
        _siteStatus = "正在读取皮肤站 API…";
        _siteStatusLiteral = false;
        if (!_queries.TryResolve(WardrobeCatalogContract.Read, out var route))
        { SetEmpty("皮肤站暂时不可用", "皮肤库服务不可用，请稍后重试。"); return; }
        _catalogReading = _queries.QueryAsync<WardrobeCatalogQuery, WardrobeCatalogPage>(route, _filter, cancellationToken: _catalogStop.Token).AsTask();
        Wake(_catalogReading, _catalogStop.Token);
    }
    private void ConsumeReplies()
    {
        if (_sitesReading is { IsCompleted: true } sites)
        {
            _sitesReading = null; _dirty = true;
            if (Succeeded(sites))
            {
                _sites = sites.Result.Value.Take(16).ToArray();
                _site = _sites.FirstOrDefault(s => s.Id == _filter.SiteId) ?? (_sites.Length > 0 ? _sites[0] : null);
                if (_site?.SupportsCapes != true) _filter = _filter with { Kind = WardrobeCatalogKind.Skin };
                BuildSites(); Reload();
            }
            else SetEmpty("没有皮肤站", "无法读取皮肤站列表，请稍后重试。");
        }
        if (_accountReading is { IsCompleted: true } account)
        {
            _accountReading = null; _accountLoaded = true; _dirty = true;
            _accountFailed = !Succeeded(account);
            if (Succeeded(account))
            {
                var reply = account.Result.Value;
                if (AdmitRead(reply)) _account = reply;
                else { _account = null; _accountFailed = true; }
            }
            // A read can refresh credentials even if the next provider operation fails.
            _seenRosterEvents = Volatile.Read(ref _rosterEvents);
        }
        if (_catalogReading is { IsCompleted: true } catalog)
        {
            _catalogReading = null; _dirty = true;
            if (Succeeded(catalog) && _site is { } site && catalog.Result.Value.SiteId == site.Id && catalog.Result.Value.Page == _filter.Page
                && catalog.Result.Value.Items.Count <= MaximumVisibleCards)
            {
                _catalog = catalog.Result.Value;
                _siteStatus = Format("第 {0} 页 · {1} 项{2}", _catalog.Page, _catalog.Items.Count,
                    string.IsNullOrWhiteSpace(_catalog.ServerVersion) ? "" : " · Blessing Skin " + _catalog.ServerVersion);
                _siteStatusLiteral = true;
                _emptyTitle = "没有找到皮肤"; _emptyMessage = "试试其他关键词或切换分类。";
                BuildCards();
            }
            else SetEmpty("皮肤站暂时不可用", "无法读取皮肤站 API，请刷新重试。");
        }
        if (_writing is { IsCompleted: true } writing)
        {
            _writing = null; _dirty = true;
            _notice = writing.IsCompletedSuccessfully && writing.Result.IsSuccess ? "外观已更新。"
                : writing.IsCompletedSuccessfully ? writing.Result.Error?.Message ?? "外观操作未完成。" : "外观操作未完成，请重试。";
            if (writing.IsCompletedSuccessfully && writing.Result.IsSuccess) _feedback.Info(_notice);
            // Always refresh admission before a retry, including provider failure after token rotation.
            _account = null; _accountLoaded = false; _seenRosterEvents = Volatile.Read(ref _rosterEvents);
            ClearImages();
            BeginAccountRead();
        }
    }

    private bool MatchesCurrentAccount(AccountWardrobeSnapshot account)
    {
        var profile = CurrentProfile();
        return profile == account.Profile && profile.Index == account.Identity.Index && profile.Kind == account.Identity.Kind && profile.Uuid == account.Identity.Uuid
            && IdentityText(profile) == _observedIdentity;
    }
    private bool AdmitRead(AccountWardrobeSnapshot account)
    {
        var roster = _store.ReadCollection<LaunchProfileView>(_store.Resolve(AccountStateContract.ProfilesKey));
        var profile = CurrentProfile();
        if (_accountReadSelectionEvents != Volatile.Read(ref _selectionEvents) || !SamePrincipal(profile, _accountReadProfile)
            || profile != account.Profile || profile.Index != account.Identity.Index
            || profile.Kind != account.Identity.Kind || profile.Uuid != account.Identity.Uuid) return false;
        if (_lastAdmittedIdentity is { } previous && (account.Identity.SelectionGeneration != previous.SelectionGeneration
            || account.Identity.RosterGeneration < previous.RosterGeneration)) return false;
        if (profile.Username != _accountReadProfile.Username
            && (roster.Revision <= _accountReadRosterRevision || (profile with { Username = _accountReadProfile.Username }) != _accountReadProfile
                || _lastAdmittedIdentity is { } earlier && account.Identity.RosterGeneration <= earlier.RosterGeneration)) return false;
        // A provider refresh can publish a new username for this admitted principal. The
        // returned view must equal the roster, and selection changes (including ABA) retire it.
        if (_accountReadSelectionEvents != Volatile.Read(ref _selectionEvents) || CurrentProfile() != profile
            || _store.ReadCollection<LaunchProfileView>(_store.Resolve(AccountStateContract.ProfilesKey)).Revision != roster.Revision) return false;
        _observedIdentity = IdentityText(profile); _lastAdmittedIdentity = account.Identity;
        return true;
    }
    private static bool SamePrincipal(LaunchProfileView current, LaunchProfileView captured) => current.Index == captured.Index
        && current.Kind == captured.Kind && current.Uuid == captured.Uuid && current.AuthServer == captured.AuthServer;
    private void Handle(DesktopUiIntent intent)
    {
        if (!Authorized(intent.Source, intent.Command)) return;
        string command = intent.Command.Value;
        if (intent.Command == CardAction)
        {
            if (_actions.TryGetValue(intent.Source, out var action)) action();
            return;
        }
        string? key = command switch
        {
            "ui.wardrobe.library.back" => "WardrobeLibraryBack",
            "ui.wardrobe.library.docs" => "WardrobeLibraryDocs",
            "ui.wardrobe.library.site" => "WardrobeLibraryOpenSite",
            "ui.wardrobe.library.refresh" => "WardrobeLibraryRefresh",
            "ui.wardrobe.library.search" => "WardrobeLibrarySearchButton",
            "ui.wardrobe.library.skin" => "WardrobeLibrarySkin",
            "ui.wardrobe.library.cape" => "WardrobeLibraryCape",
            "ui.wardrobe.library.time" => "WardrobeLibraryTime",
            "ui.wardrobe.library.likes" => "WardrobeLibraryLikes",
            "ui.wardrobe.library.previous" => "WardrobeLibraryPrevious",
            "ui.wardrobe.library.next" => "WardrobeLibraryNext",
            "ui.wardrobe.library.manage" => "WardrobeLibraryManage",
            _ => null
        };
        if (key is null || intent.Source != _nodes[key]) return;
        _dirty = true;
        if (command == "ui.wardrobe.library.back") { _back?.Invoke(); return; }
        if (command == "ui.wardrobe.library.docs") { Open(_site?.DocumentationUri); return; }
        if (command == "ui.wardrobe.library.site") { Open(_site?.BaseUri); return; }
        if (command == "ui.wardrobe.library.manage") { Open(ManageUri()); return; }
        if (_writing is not null) return;
        if (command == "ui.wardrobe.library.refresh")
        {
            _notice = null;
            _account = null; _accountLoaded = false; _accountFailed = false;
            if (_sites.Length == 0) BeginSites(); else Reload(true);
        }
        else if (command == "ui.wardrobe.library.search") Search();
        else if (command is "ui.wardrobe.library.skin" or "ui.wardrobe.library.cape")
        {
            var kind = command.EndsWith("cape", StringComparison.Ordinal) ? WardrobeCatalogKind.Cape : WardrobeCatalogKind.Skin;
            if (kind == WardrobeCatalogKind.Cape && _site?.SupportsCapes != true || kind == _filter.Kind) return;
            _filter = _filter with { Kind = kind, Page = 1 }; Reload();
        }
        else if (command is "ui.wardrobe.library.time" or "ui.wardrobe.library.likes")
        {
            var order = command.EndsWith("likes", StringComparison.Ordinal) ? WardrobeCatalogOrder.Likes : WardrobeCatalogOrder.Time;
            if (order == _filter.Order) return;
            _filter = _filter with { Order = order, Page = 1 }; Reload();
        }
        else if (command == "ui.wardrobe.library.previous" && _catalog?.HasPreviousPage == true)
        { _filter = _filter with { Page = Math.Max(1, _filter.Page - 1) }; Reload(); }
        else if (command == "ui.wardrobe.library.next" && _catalog?.HasNextPage == true)
        { _filter = _filter with { Page = _filter.Page + 1 }; Reload(); }
    }
    private void Search()
    {
        string keyword = SearchDraft().Trim();
        if (keyword == _filter.Keyword) return;
        _filter = _filter with { Keyword = keyword, Page = 1 }; Reload();
    }
    private string SearchDraft() => _shell.Tree.GetComponent<XsrUiTextInput>(_nodes["WardrobeLibrarySearch"])!.ReadDraft();
    private bool Authorized(XsrUiEntityId source, XsrSemanticId command)
    {
        if (!IsCurrent || !_shell.Tree.IsAlive(source) || _shell.Tree.GetComponent<XsrUiInput>(source) is not { Enabled: true, Clickable: true }
            || _shell.Tree.GetComponent<XsrUiCommandBinding>(source)?.Command != command) return false;
        for (var node = source; node.IsAssigned && _shell.Tree.IsAlive(node); node = _shell.Tree.Parent(node))
        {
            if (_shell.Tree.GetComponent<XsrUiElement>(node)?.IsVisible == false) return false;
            if (node == Page) return true;
        }
        return false;
    }
    private void Apply(WardrobeCatalogItem item)
    {
        if (_writing is not null || _account is not { } account || _site is not { } site || !MatchesCurrentAccount(account)
            || !CanApply(item) || _catalog?.Items.Contains(item) != true) return;
        if (!_commands.TryResolve(AccountWardrobeContract.ApplyPublic, out var route)) { _notice = "外观操作服务不可用。"; _dirty = true; return; }
        _writing = _commands.Dispatch(route, new AccountWardrobeApplyPublicCommand(account.Identity, site.Id, item.TextureId,
            item.Kind == WardrobeCatalogKind.Cape ? AccountWardrobeTextureKind.Cape : AccountWardrobeTextureKind.Skin), cancellationToken: _lifetime.Token).Completion;
        _notice = "正在更新外观…"; _dirty = true; Wake(_writing, _lifetime.Token);
    }
    private bool CanApply(WardrobeCatalogItem item) => _account is { } account && _writing is null
        && (item.Kind == WardrobeCatalogKind.Skin ? account.CanUploadSkin
            && account.Identity.Kind is LaunchProfileKind.Microsoft or LaunchProfileKind.LittleSkin or LaunchProfileKind.NCloud
            : account.Identity.Kind == LaunchProfileKind.LittleSkin && account.CanChooseCape);
    private bool ShowApply(WardrobeCatalogItem item) => item.Kind != WardrobeCatalogKind.Cape
        || _account?.Identity.Kind is not (LaunchProfileKind.Microsoft or LaunchProfileKind.NCloud);
    private Uri? ManageUri() => _account?.Identity.Kind == LaunchProfileKind.ThirdParty
        && Uri.TryCreate(_account.ManageUri, UriKind.Absolute, out var uri) && SafeBrowserUri(uri) ? uri : null;
    private static bool SafeBrowserUri(Uri uri) => uri.IsAbsoluteUri && (uri.Scheme is "https" or "http") && uri.UserInfo.Length == 0;
    private void Open(Uri? uri)
    {
        if (uri is null || !SafeBrowserUri(uri) || _openUrl is null || _effecting) return;
        _effecting = true;
        try { _openUrl(uri); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { if (IsCurrent) { _notice = "无法打开皮肤站链接，请检查默认浏览器后重试。"; _dirty = true; } }
        finally { _effecting = false; }
    }
    private void SetEmpty(string title, string message)
    {
        _emptyTitle = title; _emptyMessage = message; _siteStatus = title == "皮肤站暂时不可用" ? "API 请求失败" : message; _siteStatusLiteral = false; _dirty = true;
    }

    private void BuildSites()
    {
        Clear(_nodes["WardrobeLibrarySites"]);
        foreach (var site in _sites)
        {
            var row = Button(_nodes["WardrobeLibrarySites"], "WardrobeLibrarySite." + site.Id,
                site.DisplayName + "\n" + site.BaseUri.Host, () =>
                {
                    if (_writing is not null || _site == site) return;
                    _site = site; _filter = _filter with
                    {
                        SiteId = site.Id,
                        Page = 1,
                        Kind = site.SupportsCapes ? _filter.Kind : WardrobeCatalogKind.Skin
                    };
                    BuildSites(); Reload();
                }, 60, literal: true);
            _shell.Tree.SetComponent(row, new XsrUiSelection { IsSelected = _site == site });
            _shell.Tree.GetComponent<XsrUiVisualStyle>(row)!.WrapText = true;
            _shell.Tree.GetComponent<XsrUiVisualStyle>(row)!.Background = _site == site ? new(226, 236, 253) : XsrUiColor.Transparent;
        }
    }
    private void BuildCards()
    {
        ClearCards();
        if (_catalog is null) return;
        var items = _catalog.Items.ToArray();
        for (int index = 0; index < items.Length; index += _columns)
        {
            var row = Stack(_nodes["WardrobeLibraryItems"], "WardrobeLibraryCardRow." + index, horizontal: true);
            E(row).Height = CardHeight;
            foreach (var item in items.Skip(index).Take(_columns)) CreateCard(row, item);
        }
        var scroll = _shell.Tree.GetComponent<XsrUiScroll>(_nodes["WardrobeLibraryBody"]);
        if (scroll is not null) { scroll.OffsetX = 0; scroll.OffsetY = 0; }
        _dirty = true;
    }
    private void CreateCard(XsrUiEntityId parent, WardrobeCatalogItem item)
    {
        string prefix = "WardrobeLibraryCard." + item.TextureId.ToString(CultureInfo.InvariantCulture);
        var card = Stack(parent, prefix);
        E(card).Width = 180; E(card).Height = CardHeight - 24; E(card).Padding = new(14, 11, 14, 13);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(card)!.Background = new(248, 250, 253);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(card)!.CornerRadius = 18;
        var preview = Element(card, prefix + ".Preview", XsrUiSemanticRole.Image, item.Name);
        E(preview).Width = 84; E(preview).Height = 168; E(preview).HorizontalAlignment = XsrUiAlignment.Center;
        _shell.Tree.GetComponent<XsrUiSemantic>(preview)!.Localize = false;
        var visual = new CardVisual(item, card, preview); _cards.Add(visual);
        _shell.Tree.SetComponent(preview, new XsrUiImage("lucide/image")); PresentImage(visual);
        Text(card, prefix + ".Name", item.Name, 28, literal: true);
        Text(card, prefix + ".Uploader", Format("由 {0} 上传", string.IsNullOrWhiteSpace(item.Uploader) ? _shell.Renderer.LocalizeText("未知用户") : item.Uploader), 26, literal: true);
        Text(card, prefix + ".Metadata", item.Kind == WardrobeCatalogKind.Cape
            ? Format("♥ {0} · 披风{1}", item.Likes, item.IsHighDefinition ? " · HD" : "")
            : Format("♥ {0} · {1}{2}", item.Likes, item.Model == "alex" ? "Slim" : "Classic", item.IsHighDefinition ? " · HD" : ""), 26, literal: true);
        visual.ViewButton = Button(card, prefix + ".View", Format("{0} · 切换视角", _shell.Renderer.LocalizeText(ViewLabel(visual.View))), () =>
        {
            visual.View = (WardrobePlayerView)(((int)visual.View + 1) % 7);
            PresentImage(visual); Text(prefix + ".View", Format("{0} · 切换视角", _shell.Renderer.LocalizeText(ViewLabel(visual.View))), literal: true);
            _shell.Tree.MarkDirty(visual.Preview, XsrUiDirtyKinds.Paint); _dirty = true;
        }, literal: true);
        var apply = Button(card, prefix + ".Apply", item.Kind == WardrobeCatalogKind.Cape ? "使用披风" : "使用皮肤", () => Apply(item));
        E(apply).IsVisible = ShowApply(item); _shell.Tree.GetComponent<XsrUiInput>(apply)!.Enabled = CanApply(item);
        Button(card, prefix + ".Details", "查看详情", () =>
        {
            if (_site is { } site && item.DetailsUri.Host.Equals(site.BaseUri.Host, StringComparison.OrdinalIgnoreCase)) Open(item.DetailsUri);
        });
    }

    private void UpdateResponsiveLayout()
    {
        double width = _shell.Renderer.Viewport.Width;
        if (_viewport == _shell.Renderer.Viewport) return;
        _viewport = _shell.Renderer.Viewport;
        bool compact = _viewport.Height <= 700;
        E(_nodes["WardrobeLibraryContent"]).Padding = compact ? new(18, 10, 18, 12) : new(28, 18, 28, 20);
        _shell.Tree.GetComponent<XsrUiStackPanel>(_nodes["WardrobeLibraryContent"])!.Spacing = compact ? 6 : 12;
        E(_nodes["WardrobeLibraryHelp"]).IsVisible = !compact;
        double rail = width >= 1500 ? 294 : width <= 960 ? 238 : 268;
        if (_railWidth != rail)
        {
            _railWidth = rail;
            E(_nodes["WardrobeLibraryRail"]).Width = rail - E(_nodes["WardrobeLibraryRail"]).Padding.Horizontal;
            _shell.Tree.MarkDirty(Page, XsrUiDirtyKinds.Layout);
        }
        var padding = E(_shell.Content).Padding;
        double available = Math.Min(1320, Math.Max(208, width - 48 - padding.Left - padding.Right - rail))
            - E(_nodes["WardrobeLibraryContent"]).Padding.Horizontal;
        int columns = Math.Clamp((int)((available + 12) / 220), 1, 5);
        if (_columns == columns) return;
        _columns = columns;
        if (_cards.Count > 0)
        {
            foreach (var card in _cards) _shell.Tree.Detach(card.Container);
            Clear(_nodes["WardrobeLibraryItems"]);
            for (int index = 0; index < _cards.Count; index += _columns)
            {
                var row = Stack(_nodes["WardrobeLibraryItems"], "WardrobeLibraryCardRow." + index, horizontal: true);
                E(row).Height = CardHeight;
                foreach (var card in _cards.Skip(index).Take(_columns)) _shell.Tree.Attach(card.Container, row);
            }
        }
        _dirty = true;
    }
    private void Present()
    {
        if (!_dirty) return; _dirty = false;
        Text("WardrobeLibrarySiteName", _site?.DisplayName ?? "皮肤库", literal: _site is not null);
        Text("WardrobeLibrarySiteStatus", _siteStatus, literal: _siteStatusLiteral);
        Text("WardrobeLibraryPageNumber", Format("第 {0} 页", _filter.Page), literal: true);
        Text("WardrobeLibraryAccountNotice", _account is { } account ? account.Identity.Kind switch
        {
            LaunchProfileKind.Offline => "离线账户的外观仅供预览，不能上传或应用皮肤站外观。",
            LaunchProfileKind.ThirdParty => "第三方账户需要皮肤站的独立授权。请前往该账户对应的皮肤站管理外观。",
            LaunchProfileKind.NCloud => account.CanUploadSkin ? "可应用公开皮肤；公开披风不支持此账户。" : account.UnavailableReason ?? "此账户暂不支持应用公开皮肤。",
            LaunchProfileKind.Microsoft => "可应用公开皮肤；披风只能选择此账户已获得的披风。",
            _ => account.UnavailableReason ?? "应用外观需要皮肤站的独立授权。"
        } : _accountReading is not null ? "正在读取当前账户…"
            : _accountFailed ? "无法读取当前账户外观，请刷新重试。" : "请先添加并选择一个账户。");
        Text("WardrobeLibraryOperationStatus", _notice ?? ""); E(_nodes["WardrobeLibraryOperationStatus"]).IsVisible = !string.IsNullOrWhiteSpace(_notice);
        E(_nodes["WardrobeLibraryManage"]).IsVisible = ManageUri() is not null;
        bool loading = _sitesReading is not null || _catalogReading is not null;
        E(_nodes["WardrobeLibraryLoading"]).IsVisible = loading;
        E(_nodes["WardrobeLibraryItems"]).IsVisible = !loading && _catalog?.Items.Count > 0;
        E(_nodes["WardrobeLibraryEmpty"]).IsVisible = !loading && _catalog?.Items.Count is not > 0;
        Text("WardrobeLibraryEmptyTitle", _emptyTitle); Text("WardrobeLibraryEmptyMessage", _emptyMessage);
        bool idle = _writing is null;
        Enable("WardrobeLibraryBack", _back is not null);
        Enable("WardrobeLibraryDocs", _site is not null && _openUrl is not null);
        Enable("WardrobeLibraryOpenSite", _site is not null && _openUrl is not null);
        Enable("WardrobeLibraryManage", ManageUri() is not null && _openUrl is not null);
        Enable("WardrobeLibraryRefresh", idle && !loading);
        foreach (string key in new[] { "WardrobeLibrarySearchButton", "WardrobeLibrarySkin", "WardrobeLibraryCape", "WardrobeLibraryTime", "WardrobeLibraryLikes" }) Enable(key, idle && _site is not null);
        E(_nodes["WardrobeLibraryCape"]).IsVisible = _site?.SupportsCapes == true;
        Enable("WardrobeLibraryPrevious", idle && !loading && _catalog?.HasPreviousPage == true);
        Enable("WardrobeLibraryNext", idle && !loading && _catalog?.HasNextPage == true);
        Select("WardrobeLibrarySkin", _filter.Kind == WardrobeCatalogKind.Skin); Select("WardrobeLibraryCape", _filter.Kind == WardrobeCatalogKind.Cape);
        Select("WardrobeLibraryTime", _filter.Order == WardrobeCatalogOrder.Time); Select("WardrobeLibraryLikes", _filter.Order == WardrobeCatalogOrder.Likes);
        if (_catalog is not null)
            foreach (var item in _catalog.Items.Take(MaximumVisibleCards))
            {
                string key = "WardrobeLibraryCard." + item.TextureId.ToString(CultureInfo.InvariantCulture) + ".Apply";
                if (_nodes.TryGetValue(key, out var button)) { E(button).IsVisible = ShowApply(item); _shell.Tree.GetComponent<XsrUiInput>(button)!.Enabled = CanApply(item); }
            }
        _shell.Tree.MarkDirty(Page, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private void Retire()
    {
        _lifetime.Cancel(); _lifetime.Dispose(); _lifetime = new();
        _catalogStop.Cancel(); _catalogStop.Dispose(); _catalogStop = new();
        _sitesReading = null; _catalogReading = null; _accountReading = null; _writing = null;
        _account = null; _lastAdmittedIdentity = null; _catalog = null; _started = false; _accountLoaded = false; _accountFailed = false; _notice = null; _dirty = true;
        _siteStatus = "正在读取皮肤站 API…"; _siteStatusLiteral = false; _scrollOffset = double.NaN;
        while (_pending.TryDequeue(out _)) { }
        ClearCards();
    }
    private void ClearCards() { ClearImages(); Clear(_nodes["WardrobeLibraryItems"]); _cards.Clear(); }
    private void ConsumeImages()
    {
        if (_cards.Count == 0 || _site is null) return;
        double offset = _shell.Tree.GetComponent<XsrUiScroll>(_nodes["WardrobeLibraryBody"])!.OffsetY;
        double height = _shell.Renderer.TryGetScrollSnapshot(_nodes["WardrobeLibraryBody"], out var snapshot)
            ? snapshot.ViewportHeight : CardHeight;
        int firstRow = Math.Clamp((int)(offset / (CardHeight + RowSpacing)), 0, (_cards.Count - 1) / _columns);
        int lastRow = Math.Max(firstRow, (int)Math.Ceiling((offset + Math.Max(1, height)) / (CardHeight + RowSpacing)) - 1);
        var visible = _cards.Skip(firstRow * _columns).Take((lastRow - firstRow + 1) * _columns).ToHashSet();
        for (int index = _images.Count - 1; index >= 0; index--)
        {
            var image = _images[index];
            if (!visible.Contains(image.Card))
            {
                image.Stop.Cancel(); image.Stop.Dispose(); _images.RemoveAt(index); image.Card.Attempted = false;
                continue;
            }
            if (!image.Read.IsCompleted) continue;
            _images.RemoveAt(index);
            image.Stop.Dispose();
            if (!Succeeded(image.Read) || !_shell.Tree.IsAlive(image.Card.Preview)) continue;
            image.Card.Loaded = image.Read.Result.Value; PresentImage(image.Card);
            _shell.Tree.MarkDirty(image.Card.Preview, XsrUiDirtyKinds.Paint);
        }
        foreach (var card in _cards)
        {
            if (visible.Contains(card)) continue;
            card.Attempted = false;
            if (card.Loaded is null) continue;
            card.Loaded = null; PresentImage(card); _shell.Tree.MarkDirty(card.Preview, XsrUiDirtyKinds.Paint);
        }
        if (!_queries.TryResolve(WardrobeCatalogContract.Preview, out var route)) return;
        foreach (var card in visible.OrderBy(card => _cards.IndexOf(card)))
        {
            if (_images.Count >= 4) break;
            if (card.Attempted || !_shell.Tree.IsAlive(card.Preview)) continue;
            card.Attempted = true;
            var stop = CancellationTokenSource.CreateLinkedTokenSource(_imageStop.Token);
            var query = new WardrobeCatalogPreviewQuery(_site.Id, card.Item.TextureId, card.Item.Kind);
            var read = _queries.QueryAsync<WardrobeCatalogPreviewQuery, AccountWardrobeResolvedTextures>(route, query, cancellationToken: stop.Token).AsTask();
            _images.Add(new(card, stop, read)); Wake(read, stop.Token);
        }
    }
    private void PresentImage(CardVisual card) => _shell.Tree.GetComponent<XsrUiImage>(card.Preview)!.Raster
        = WardrobePlayerPresentation.Player(card.Loaded?.Skin, card.Loaded?.Cape, card.Item.Model == "alex", card.View);
    private static string ViewLabel(WardrobePlayerView view) => view switch
    { WardrobePlayerView.Isometric => "立体", WardrobePlayerView.Front => "正面", WardrobePlayerView.Back => "背面", WardrobePlayerView.Left => "左侧", WardrobePlayerView.Right => "右侧", WardrobePlayerView.Top => "顶部", _ => "底部" };
    private void ClearImages()
    {
        _imageStop.Cancel(); _imageStop.Dispose(); _imageStop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        foreach (var image in _images) image.Stop.Dispose();
        _images.Clear();
        foreach (var card in _cards) { card.Loaded = null; card.Attempted = false; if (_shell.Tree.IsAlive(card.Preview)) PresentImage(card); }
    }
    private void Clear(XsrUiEntityId parent)
    {
        foreach (var child in _shell.Tree.Children(parent).ToArray())
        {
            _shell.Tree.Walk(child, entity =>
            {
                _actions.Remove(entity);
                string name = _shell.Tree.Name(entity); if (_nodes.TryGetValue(name, out var mapped) && mapped == entity) _nodes.Remove(name);
                return true;
            });
            _shell.Tree.Destroy(child);
        }
    }
    private void Wake(Task task, CancellationToken token) => _ = task.ContinueWith(completed =>
    {
        _ = completed.Exception;
        if (_disposed || token.IsCancellationRequested) return;
        _dirty = true;
        try { _store.Publish(_store.Resolve(WardrobePresentationState.WakeKey), Interlocked.Increment(ref _wake)); }
        catch (ObjectDisposedException) { }
    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static bool Succeeded<T>(Task<XsrResult<T>> task) => task.IsCompletedSuccessfully && task.Result.IsSuccess;
    private string Format(string source, params object[] arguments) => string.Format(CultureInfo.CurrentCulture, _shell.Renderer.LocalizeText(source), arguments);
    private XsrUiElement E(XsrUiEntityId entity) => _shell.Tree.GetComponent<XsrUiElement>(entity)!;
    private XsrUiEntityId Element(XsrUiEntityId parent, string name, XsrUiSemanticRole role = XsrUiSemanticRole.None, string? label = null)
    {
        var entity = _shell.Tree.Create(name); _shell.Tree.Attach(entity, parent); _nodes[name] = entity;
        _shell.Tree.SetComponent(entity, new XsrUiElement()); _shell.Tree.SetComponent(entity, new XsrUiSemantic(role, label)); Style(entity); return entity;
    }
    private XsrUiEntityId Stack(XsrUiEntityId parent, string name, bool horizontal = false)
    {
        var entity = Element(parent, name); _shell.Tree.SetComponent(entity, new XsrUiStackPanel(horizontal ? XsrUiOrientation.Horizontal : XsrUiOrientation.Vertical) { Spacing = horizontal ? 12 : 3 }); return entity;
    }
    private XsrUiEntityId Button(XsrUiEntityId parent, string name, string label, Action action, double height = 31, bool literal = false)
    {
        var entity = Element(parent, name, XsrUiSemanticRole.Button, label); E(entity).Height = height;
        _shell.Tree.SetComponent(entity, new XsrUiText(label));
        _shell.Tree.SetComponent(entity, new XsrUiInput { Clickable = true, Focusable = true });
        _shell.Tree.SetComponent(entity, new XsrUiCommandBinding(CardAction)); _actions[entity] = action; Style(entity);
        if (literal) DesktopLiteralText.Preserve(_shell.Tree, entity);
        return entity;
    }
    private void Text(XsrUiEntityId parent, string name, string value, double height, bool literal = false)
    {
        var entity = Element(parent, name, XsrUiSemanticRole.Text, value); E(entity).Height = height;
        _shell.Tree.SetComponent(entity, new XsrUiText(value) { MaxLines = 1, TrimOverflow = true });
        _shell.Tree.GetComponent<XsrUiVisualStyle>(entity)!.TextAlignment = XsrUiTextAlignment.Center;
        if (literal) DesktopLiteralText.Preserve(_shell.Tree, entity);
    }
    private void Text(string name, string value, bool literal = false)
    {
        var entity = _nodes[name]; var text = _shell.Tree.GetComponent<XsrUiText>(entity)!;
        text.Content = value; text.Localize = !literal;
        if (_shell.Tree.GetComponent<XsrUiSemantic>(entity) is { } semantic) { semantic.Label = value; semantic.Localize = !literal; }
    }
    private void Enable(string name, bool enabled) => _shell.Tree.GetComponent<XsrUiInput>(_nodes[name])!.Enabled = enabled;
    private void Select(string name, bool selected) => _shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes[name])!.Background = selected ? new(226, 236, 253) : new(241, 245, 250);
    private void Style(XsrUiEntityId entity)
    {
        bool button = _shell.Tree.GetComponent<XsrUiInput>(entity)?.Clickable == true;
        _shell.Tree.SetComponent(entity, new XsrUiVisualStyle
        {
            Foreground = new(43, 51, 64),
            Background = button ? new(241, 245, 250) : XsrUiColor.Transparent,
            Hover = new(226, 236, 253),
            CornerRadius = button ? 10 : 0,
            FontSize = 13,
            WrapText = !button,
            TextAlignment = button ? XsrUiTextAlignment.Center : XsrUiTextAlignment.Start
        });
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _intents.IntentEmitted -= OnIntent; _shell.Renderer.FramePreparing -= OnFrame; _store.Changed -= OnStateChanged;
        Retire(); _lifetime.Dispose(); _catalogStop.Dispose(); _imageStop.Dispose();
        if (_shell.Tree.IsAlive(Page)) _shell.Tree.Destroy(Page);
    }
}
