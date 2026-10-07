using System.Collections.Concurrent;
using System.Globalization;
using Nexa.Pxml;
using Nexa.Services.Accounts;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

internal static class WardrobePresentationState
{
    internal static readonly XsrSemanticId WakeKey = XsrSemanticId.Parse("ui.wardrobe.wake");
    internal static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<long>(WakeKey, "Nexa.Desktop.Wardrobe");
}

/// <summary>Render-thread projection of the dev wardrobe behavior, with admitted service intents.</summary>
internal sealed class WardrobePageController : IDisposable
{
    private sealed class CardVisual(AccountWardrobeCard card, XsrUiEntityId container, XsrUiEntityId body,
        XsrUiEntityId image, XsrUiEntityId details, XsrUiEntityId viewButton, XsrUiEntityId apply)
    {
        internal readonly AccountWardrobeCard Card = card;
        internal readonly XsrUiEntityId Container = container, Body = body, Image = image, Details = details, ViewButton = viewButton, Apply = apply;
        internal WardrobePlayerView View = card.Kind == AccountWardrobeTextureKind.Cape ? WardrobePlayerView.Back : WardrobePlayerView.Front;
        internal AccountWardrobeResolvedTextures? Loaded;
        internal AccountWardrobeResolvedTextures? Presented;
        internal WardrobePlayerView PresentedView;
    }
    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly XsrQueryRouter _queries;
    private readonly XsrCommandRouter _commands;
    private readonly XsrStateStore _store;
    private readonly DesktopFeedbackService _feedback;
    private readonly Dictionary<string, XsrUiEntityId> _nodes = [];
    private readonly Dictionary<string, HashSet<XsrUiEntityId>> _staticSources = [];
    private readonly Dictionary<XsrUiEntityId, CardVisual> _applySources = [], _viewSources = [];
    private readonly List<CardVisual> _skinCards = [], _capeCards = [];
    private readonly Dictionary<CardVisual, Task<XsrResult<AccountWardrobeResolvedTextures>>> _textures = [];
    private readonly HashSet<CardVisual> _textureAttempts = [];
    private readonly ConcurrentQueue<DesktopUiIntent> _pending = new();
    private CancellationTokenSource _stop = new();
    private Func<CancellationToken, Task<string?>>? _pickFile;
    private Action? _openLibrary;
    private Action<Uri>? _openUrl;
    private Task<string?>? _picking;
    private Task<XsrResult<AccountWardrobeSnapshot>>? _reading;
    private Task<XsrResult<AccountWardrobeSkinPreview>>? _previewing;
    private Task<XsrResult>? _writing;
    private AccountWardrobeSnapshot? _snapshot;
    private AccountWardrobeSkinPreview? _preview;
    private AccountWardrobeResolvedTextures? _presentedCurrent;
    private WardrobePlayerView _currentView = WardrobePlayerView.Front, _presentedCurrentView;
    private string? _observedIdentity;
    private LaunchProfileView? _readingProfile;
    private long _readingSelectionEpoch;
    private LaunchProfileView? _writingProfile;
    private long _writingSelectionEpoch;
    private bool _visible, _loaded, _slim, _disposed, _localOpen;
    private volatile bool _dirty = true;
    private long _wake, _selectionEvents, _seenSelectionEvents, _rosterEvents, _seenRosterEvents;
    private string _status = "正在读取当前账户…";
    private string? _operationNotice;
    private XsrUiSize _viewport;
    private double _skinOffset, _capeOffset, _cardWidth = 176, _cardHeight = 228, _trackWidth = 400;
    private bool Busy => _reading is not null || _previewing is not null || _writing is not null || _picking is not null;
    internal const int CapePageSize = 12; // Bounded lazy image window; the horizontal track retains every card.

    internal WardrobePageController(XsrUiShell shell, DesktopUiIntentSink intents, XsrQueryRouter queries,
        XsrCommandRouter commands, XsrStateStore store, DesktopFeedbackService feedback)
    {
        _shell = shell; _intents = intents; _queries = queries; _commands = commands; _store = store; _feedback = feedback;
        using var stream = typeof(WardrobePageController).Assembly.GetManifestResourceStream("Nexa.Desktop.Ui.WardrobePage.pxml")!;
        using var reader = new StreamReader(stream);
        var host = shell.Tree.Create("wardrobe-loader");
        Page = PxmlUiLoader.Load(PxmlCompiler.Compile(PxmlParser.Parse(reader.ReadToEnd())), shell.Tree, store, host);
        shell.Tree.Detach(Page); shell.Tree.Destroy(host);
        shell.Tree.Walk(Page, entity =>
        {
            string name = shell.Tree.Name(entity); if (name.Length > 0) _nodes[name] = entity;
            if (shell.Tree.GetComponent<XsrUiCommandBinding>(entity) is { } binding)
            {
                if (!_staticSources.TryGetValue(binding.Command.Value, out var sources)) _staticSources[binding.Command.Value] = sources = [];
                sources.Add(entity);
            }
            Style(entity); return true;
        });
        shell.Tree.SetComponent(_nodes["WardrobeLocalLayer"], new XsrUiOverlayLayer(isModal: true));
        shell.Tree.SetComponent(_nodes["WardrobeLocalLayer"], new XsrUiDismissBinding(XsrSemanticId.Parse("ui.wardrobe.local.close")));
        shell.Tree.SetComponent(_nodes["WardrobeLocalLayer"], new XsrUiCommandBinding(XsrSemanticId.Parse("ui.wardrobe.local.close")));
        _staticSources["ui.wardrobe.local.close"].Add(_nodes["WardrobeLocalLayer"]);
        shell.Tree.SetComponent(_nodes["WardrobeLocalLayer"], new XsrUiInput { Clickable = true });
        Scroll("WardrobeSkins").UseVerticalWheelForHorizontalScroll = true;
        Scroll("WardrobeCapes").UseVerticalWheelForHorizontalScroll = true;
        shell.Tree.SetComponent(_nodes["WardrobeContent"], new XsrUiScrollGesture());
        shell.Tree.SetComponent(_nodes["WardrobeProfileRail"], new XsrUiScrollGesture());
        shell.Tree.SetComponent(_nodes["WardrobeLocalPanel"], new XsrUiScrollGesture());
        shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes["WardrobeLocalLayer"])!.Background = new(0, 0, 0, 100);
        shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes["WardrobeLocalPanel"])!.Background = new(250, 252, 255);
        shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes["WardrobeLocalPanel"])!.CornerRadius = 18;
        foreach (string key in new[] { "WardrobeCurrentTitle", "WardrobePageTitle", "WardrobeSkinTitle", "WardrobeCapeTitle", "WardrobeLocalTitle" })
        { var style = shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes[key])!; style.FontSize = 19; style.FontWeight = 600; }
        foreach (string key in new[] { "WardrobeCurrentSubtitle", "WardrobeProfileKind", "WardrobeSkinSubtitle", "WardrobeCapeSubtitle", "WardrobeSkinCount", "WardrobeCapeCount", "WardrobeStatus", "WardrobeLocalStatus" })
        { var style = shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes[key])!; style.FontSize = 12; style.Foreground = new(105, 114, 128); }
        shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes["WardrobeProfileRail"])!.Background = new(244, 247, 251);
        var currentStyle = shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes["WardrobeCurrentCard"])!;
        currentStyle.Background = new(255, 255, 255); currentStyle.CornerRadius = 22;
        foreach (string key in new[] { "WardrobeIdentity", "WardrobeProfileKind" })
        {
            shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes[key])!.TextAlignment = XsrUiTextAlignment.Center;
            var text = shell.Tree.GetComponent<XsrUiText>(_nodes[key])!; text.MaxLines = 1; text.TrimOverflow = true;
        }
        var identityStyle = shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes["WardrobeIdentity"])!;
        identityStyle.FontSize = 17; identityStyle.FontWeight = 600;
        _intents.IntentEmitted += OnIntent; shell.Renderer.FramePreparing += OnFrame; _store.Changed += OnStateChanged;
    }
    internal XsrUiEntityId Page { get; }
    internal XsrUiEntityId LibraryButton => _nodes["WardrobeLibrary"];
    internal void ConfigureFilePicker(Func<CancellationToken, Task<string?>> picker) => _pickFile = picker;
    internal void ConfigureLibrary(Action openLibrary) => _openLibrary = openLibrary;
    internal void ConfigureOpenUrl(Action<Uri> openUrl) => _openUrl = openUrl;

    private void OnIntent(object? sender, DesktopUiIntentEventArgs args)
    {
        if (!_disposed && _shell.Stage.Navigation.Current == Page && args.Intent.Command.Value.StartsWith("ui.wardrobe.", StringComparison.Ordinal))
            _pending.Enqueue(args.Intent);
    }
    private void OnStateChanged(XsrStateChange change)
    {
        if (change.SemanticId == AccountStateContract.SelectedKey) Interlocked.Increment(ref _selectionEvents);
        if (change.SemanticId == AccountStateContract.ProfilesKey) Interlocked.Increment(ref _rosterEvents);
        if (change.SemanticId == AccountStateContract.ProfilesKey || change.SemanticId == AccountStateContract.SelectedKey
            || change.SemanticId == AccountSkinContract.SkinsKey) _dirty = true;
    }
    private LaunchProfileView CurrentProfile()
    {
        int selected = _store.Read<int>(_store.Resolve(AccountStateContract.SelectedKey)).Value;
        return _store.ReadCollection<LaunchProfileView>(_store.Resolve(AccountStateContract.ProfilesKey)).Items.FirstOrDefault(p => p.Index == selected);
    }
    private string? CurrentIdentity()
    {
        var profile = CurrentProfile();
        return string.IsNullOrWhiteSpace(profile.Username) ? null
            : $"{profile.Index}\n{profile.Kind}\n{profile.Uuid}\n{profile.AuthServer}\n{profile.Username}";
    }
    private void OnFrame(object? sender, EventArgs args)
    {
        if (_disposed) return;
        bool visible = _shell.Stage.Navigation.Current == Page;
        if (!visible) { if (_visible) Retire(); _visible = false; return; }
        double skinOffset = Scroll("WardrobeSkins").OffsetX, capeOffset = Scroll("WardrobeCapes").OffsetX;
        double trackWidth = _trackWidth;
        if (_shell.Renderer.TryGetScrollSnapshot(_nodes["WardrobeSkins"], out var skinTrack) && skinTrack.ViewportWidth > 0)
            trackWidth = skinTrack.ViewportWidth;
        else if (_shell.Renderer.TryGetScrollSnapshot(_nodes["WardrobeCapes"], out var capeTrack) && capeTrack.ViewportWidth > 0)
            trackWidth = capeTrack.ViewportWidth;
        if (_viewport != _shell.Renderer.Viewport || _skinOffset != skinOffset || _capeOffset != capeOffset || _trackWidth != trackWidth) _dirty = true;
        _trackWidth = trackWidth;
        long selections = Volatile.Read(ref _selectionEvents), rosters = Volatile.Read(ref _rosterEvents);
        if (_visible && (_loaded || _observedIdentity is null) && !_dirty && _pending.IsEmpty
            && selections == _seenSelectionEvents && rosters == _seenRosterEvents
            && _picking is not { IsCompleted: true } && _reading is not { IsCompleted: true }
            && _previewing is not { IsCompleted: true } && _writing is not { IsCompleted: true }
            && !_textures.Values.Any(task => task.IsCompleted)) return;
        string? identity = CurrentIdentity();
        bool rosterChangedWhileIdle = rosters != _seenRosterEvents && _reading is null && _writing is null;
        bool admittedReadContext = _reading is not null && _readingProfile is { } readingProfile
            && selections == _readingSelectionEpoch && SamePrincipal(readingProfile, CurrentProfile());
        bool admittedWriteContext = _writing is not null && _writingProfile is { } writingProfile
            && selections == _writingSelectionEpoch && SamePrincipal(writingProfile, CurrentProfile());
        if (!_visible || identity != _observedIdentity && !admittedReadContext && !admittedWriteContext
            || selections != _seenSelectionEvents || rosterChangedWhileIdle)
        {
            Retire(); _observedIdentity = identity;
            _status = identity is null ? "请先添加并选择一个账户。" : "正在读取当前账户…";
        }
        _visible = true; _seenSelectionEvents = selections; _seenRosterEvents = rosters;
        ApplyResponsiveLayout();
        if (identity is null) { Present(); return; }
        while (_pending.TryDequeue(out var intent))
        {
            Handle(intent);
            if (_disposed || _shell.Stage.Navigation.Current != Page) return;
        }
        if (_picking is { IsCompleted: true } picking)
        {
            _dirty = true; _picking = null;
            if (picking.IsCompletedSuccessfully && picking.Result is { } path)
            { _shell.Renderer.SetTextInputValue(_nodes["WardrobeSkinPath"], path); BeginPreview(); }
            else if (picking.IsFaulted) _status = "无法选择皮肤文件，请重试。";
        }
        if (!_loaded && _reading is null) BeginRead();
        if (_reading is { IsCompleted: true } reading)
        {
            _dirty = true; _reading = null; _loaded = true;
            if (Succeeded(reading) && MatchesPublishedProfile(reading.Result.Value))
            {
                _snapshot = reading.Result.Value;
                _status = _operationNotice ?? _snapshot.ProviderStatus ?? _snapshot.UnavailableReason ?? "选择皮肤或已获得的披风。";
                BuildCards();
            }
            else
                _status = reading.IsCompletedSuccessfully && !reading.Result.IsSuccess
                    ? reading.Result.Error?.Message ?? "无法读取更衣橱。" : "无法读取更衣橱，请重试。";
            // An admitted read can publish a provider rename before its final reply, including
            // a later failure. Consume that same-principal roster change without auto-retrying.
            if (_readingProfile is { } captured && SamePrincipal(captured, CurrentProfile())
                && Volatile.Read(ref _selectionEvents) == _readingSelectionEpoch)
                _observedIdentity = CurrentIdentity();
            _readingProfile = null;
            _seenRosterEvents = Volatile.Read(ref _rosterEvents);
        }
        if (_previewing is { IsCompleted: true } previewing)
        {
            _dirty = true; _previewing = null;
            if (Succeeded(previewing)) { _preview = previewing.Result.Value; _status = "皮肤已验证，可以上传。"; }
            else _status = previewing.IsCompletedSuccessfully ? previewing.Result.Error?.Message ?? "皮肤无法使用。" : "无法读取皮肤文件。";
        }
        if (_writing is { IsCompleted: true } writing)
        {
            _dirty = true; _writing = null;
            bool currentWrite = _writingProfile is { } captured && SamePrincipal(captured, CurrentProfile())
                && Volatile.Read(ref _selectionEvents) == _writingSelectionEpoch;
            if (!currentWrite)
            {
                Retire(); _observedIdentity = CurrentIdentity(); _status = "正在读取当前账户…";
            }
            else if (writing.IsCompletedSuccessfully && writing.Result.IsSuccess)
            {
                _operationNotice = null; _feedback.Info("外观已更新。"); _loaded = false; _snapshot = null; _preview = null; _localOpen = false;
                _shell.Renderer.SetTextInputValue(_nodes["WardrobeSkinPath"], ""); _status = "外观已更新，正在刷新…";
            }
            else
            {
                _operationNotice = _status = writing.IsCompletedSuccessfully ? writing.Result.Error?.Message ?? "外观操作未完成。" : "外观操作未完成，请重试。";
                _loaded = false; _snapshot = null;
            }
            if (currentWrite) _observedIdentity = CurrentIdentity();
            _writingProfile = null;
            _seenRosterEvents = Volatile.Read(ref _rosterEvents);
        }
        PumpTextures(); Present();
    }
    private bool AdmittedSource(DesktopUiIntent intent)
    {
        if (!intent.Source.IsAssigned || !_shell.Tree.IsAlive(intent.Source)
            || _shell.Tree.GetComponent<XsrUiInput>(intent.Source) is not { Enabled: true }
            || _shell.Tree.GetComponent<XsrUiCommandBinding>(intent.Source)?.Command != intent.Command) return false;
        bool inLocal = false;
        for (var entity = intent.Source; entity.IsAssigned; entity = _shell.Tree.Parent(entity))
        {
            if (_shell.Tree.GetComponent<XsrUiElement>(entity) is { IsVisible: false }) return false;
            inLocal |= entity == _nodes["WardrobeLocalLayer"];
            if (entity == Page) return !_localOpen || inLocal;
        }
        return false;
    }
    private void Handle(DesktopUiIntent intent)
    {
        if (!AdmittedSource(intent)) return;
        string command = intent.Command.Value;
        bool knownStatic = _staticSources.TryGetValue(command, out var sources) && sources.Contains(intent.Source);
        bool knownView = command == "ui.wardrobe.view.card" && _viewSources.ContainsKey(intent.Source);
        bool knownApply = _applySources.TryGetValue(intent.Source, out var candidate)
            && command == (candidate.Card.Kind == AccountWardrobeTextureKind.Cape ? "ui.wardrobe.cape.choose" : "ui.wardrobe.skin.choose");
        if (!knownStatic && !knownApply && !knownView) return;
        _dirty = true;
        if (command is "ui.wardrobe.cancel" or "ui.wardrobe.local.close")
        {
            if (Busy) { Retire(); _observedIdentity = CurrentIdentity(); _loaded = true; _status = "操作已取消。点击刷新重新读取外观。"; }
            else { _localOpen = false; _preview = null; _shell.Renderer.SetTextInputValue(_nodes["WardrobeSkinPath"], ""); }
            return;
        }
        if (command == "ui.wardrobe.refresh") { if (!Busy) { Retire(); _observedIdentity = CurrentIdentity(); } return; }
        if (command == "ui.wardrobe.view.current") { _currentView = WardrobePlayerPresentation.Next(_currentView); return; }
        if (command == "ui.wardrobe.view.card" && _viewSources.TryGetValue(intent.Source, out var viewed))
        { viewed.View = WardrobePlayerPresentation.Next(viewed.View); return; }
        if (Busy || _snapshot is not { } snapshot) return;
        _operationNotice = null;
        if (command == "ui.wardrobe.library" && _openLibrary is not null) { _openLibrary(); return; }
        if (command == "ui.wardrobe.manage" && SafeManageUri(snapshot.ManageUri) is { } uri && _openUrl is not null)
        {
            try { _openUrl(uri); }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
            { _status = "无法打开账户皮肤站，请重试。"; }
            return;
        }
        if (command is "ui.wardrobe.skin.previous" or "ui.wardrobe.skin.next" or "ui.wardrobe.cape.previous" or "ui.wardrobe.cape.next")
        {
            string track = command.Contains(".skin.", StringComparison.Ordinal) ? "WardrobeSkins" : "WardrobeCapes";
            var scroll = Scroll(track);
            scroll.OffsetX = Math.Max(0, scroll.OffsetX + (command.EndsWith("next", StringComparison.Ordinal) ? 1 : -1) * Math.Max(_cardWidth + 12, _trackWidth - 12));
            _shell.Tree.MarkDirty(_nodes[track], XsrUiDirtyKinds.Layout); return;
        }
        if (command is "ui.wardrobe.classic" or "ui.wardrobe.slim")
        { _slim = command.EndsWith("slim", StringComparison.Ordinal); _preview = null; _status = "模型已改变，请重新校验皮肤。"; }
        else if (command == "ui.wardrobe.browse")
        {
            if (snapshot.CanUploadSkin && _pickFile is not null)
            {
                _localOpen = true;
                try { _picking = _pickFile(_stop.Token); Wake(_picking); }
                catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
                { _status = "无法选择皮肤文件，请重试。"; }
            }
            else if (SafeManageUri(snapshot.ManageUri) is { } site && _openUrl is not null)
            {
                try { _openUrl(site); }
                catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
                { _status = "无法打开账户皮肤站，请重试。"; }
            }
            else _status = snapshot.UnavailableReason ?? "当前账户不支持在启动器内更换皮肤。";
        }
        else if (command == "ui.wardrobe.preview" && snapshot.CanUploadSkin) BeginPreview();
        else if (command == "ui.wardrobe.upload" && snapshot.CanUploadSkin && _preview is { } preview)
            Write(AccountWardrobeContract.UploadSkin, new AccountWardrobeUploadSkinCommand(snapshot.Identity,
                preview.Image.Bytes.ToArray(), preview.FileName, preview.IsSlim));
        else if (command == "ui.wardrobe.cape.clear" && snapshot.CanChooseCape)
            Write(AccountWardrobeContract.SetCape, new AccountWardrobeSetCapeCommand(snapshot.Identity, null));
        else if (command is "ui.wardrobe.skin.choose" or "ui.wardrobe.cape.choose"
            && _applySources.TryGetValue(intent.Source, out var chosen) && chosen.Card.CanApply && !chosen.Card.IsActive)
        {
            if (chosen.Card.Kind == AccountWardrobeTextureKind.Cape && !snapshot.CanChooseCape) return;
            if (chosen.Card.Kind == AccountWardrobeTextureKind.Cape && chosen.Card.CapeId is { } id)
                Write(AccountWardrobeContract.SetCape, new AccountWardrobeSetCapeCommand(snapshot.Identity, id));
            else Write(AccountWardrobeContract.ApplyCard, new AccountWardrobeApplyCardCommand(snapshot.Identity, chosen.Card.Id));
        }
    }
    private static Uri? SafeManageUri(string? address) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 && !uri.IsLoopback ? uri : null;
    private void BeginRead()
    {
        _dirty = true; _status = _operationNotice ?? "正在读取当前账户…";
        if (!_queries.TryResolve(AccountWardrobeContract.Read, out var route)) { _loaded = true; _status = "更衣橱服务不可用。"; return; }
        _readingProfile = CurrentProfile(); _readingSelectionEpoch = Volatile.Read(ref _selectionEvents);
        _reading = _queries.QueryAsync<AccountWardrobeQuery, AccountWardrobeSnapshot>(route, new(), cancellationToken: _stop.Token).AsTask(); Wake(_reading);
    }
    private bool MatchesPublishedProfile(AccountWardrobeSnapshot snapshot)
    {
        var published = CurrentProfile();
        return _readingProfile is { } captured && SamePrincipal(captured, published)
            && Volatile.Read(ref _selectionEvents) == _readingSelectionEpoch
            && snapshot.Profile == published && snapshot.Identity.Index == published.Index
            && snapshot.Identity.Uuid == published.Uuid && snapshot.Identity.Kind == published.Kind;
    }
    private static bool SamePrincipal(LaunchProfileView captured, LaunchProfileView published) =>
        !string.IsNullOrWhiteSpace(published.Username) && captured.Index == published.Index
        && captured.Uuid == published.Uuid && captured.Kind == published.Kind && captured.AuthServer == published.AuthServer;
    private void BeginPreview()
    {
        _preview = null;
        if (_snapshot is not { } snapshot || !_queries.TryResolve(AccountWardrobeContract.ValidateSkin, out var route)) return;
        string path = _shell.Tree.GetComponent<XsrUiTextInput>(_nodes["WardrobeSkinPath"])!.ReadDraft();
        _previewing = _queries.QueryAsync<AccountWardrobeSkinQuery, AccountWardrobeSkinPreview>(route,
            new(snapshot.Identity, path, _slim), cancellationToken: _stop.Token).AsTask(); Wake(_previewing); _status = "正在校验皮肤…";
    }
    private void Write<T>(XsrSemanticId semantic, T command) where T : notnull
    {
        if (!_commands.TryResolve(semantic, out var route)) { _status = "外观操作服务不可用。"; return; }
        _writingProfile = CurrentProfile(); _writingSelectionEpoch = Volatile.Read(ref _selectionEvents);
        _writing = _commands.Dispatch(route, command, cancellationToken: _stop.Token).Completion; Wake(_writing); _status = "正在更新外观…";
    }
    private void Wake(Task task)
    {
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            if (_disposed) return;
            _dirty = true;
            try { _store.Publish(_store.Resolve(WardrobePresentationState.WakeKey), Interlocked.Increment(ref _wake)); }
            catch (ObjectDisposedException) { }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private static bool Succeeded<T>(Task<XsrResult<T>> task) => task.IsCompletedSuccessfully && task.Result.IsSuccess;
    private void PumpTextures()
    {
        foreach (var (card, pending) in _textures.ToArray())
            if (pending.IsCompleted)
            {
                _textures.Remove(card);
                if (Succeeded(pending)) card.Loaded = pending.Result.Value;
                _dirty = true;
            }
        var visible = VisibleCards(_skinCards, Scroll("WardrobeSkins").OffsetX)
            .Concat(VisibleCards(_capeCards, Scroll("WardrobeCapes").OffsetX)).ToHashSet();
        foreach (var card in _skinCards.Concat(_capeCards))
            if (!visible.Contains(card) && !_textures.ContainsKey(card) && card.Loaded is not null)
            { card.Loaded = null; _textureAttempts.Remove(card); card.Presented = null; }
        if (_localOpen || Busy || _snapshot is not { } snapshot || !_queries.TryResolve(AccountWardrobeContract.ReadTexture, out var route)) return;
        foreach (var card in visible)
        {
            if (_textures.Count >= 3) break;
            if (!_textureAttempts.Add(card) || card.Card.Appearance.Skin is not null &&
                (card.Card.Appearance.CapeAddress is null || card.Card.Appearance.Cape is not null)) continue;
            var pending = _queries.QueryAsync<AccountWardrobeTextureQuery, AccountWardrobeResolvedTextures>(route,
                new(snapshot.Identity, CardId: card.Card.Id, Kind: card.Card.Kind), cancellationToken: _stop.Token).AsTask();
            _textures.Add(card, pending); Wake(pending);
        }
    }
    private IEnumerable<CardVisual> VisibleCards(List<CardVisual> cards, double offset) => cards
        .Skip(Math.Clamp((int)(offset / (_cardWidth + 12)), 0, Math.Max(0, cards.Count - 1))).Take(CapePageSize);
    private void Present()
    {
        if (!_dirty) return; _dirty = false;
        var profile = _writing is not null && _writingProfile is { } captured && SamePrincipal(captured, CurrentProfile())
            ? CurrentProfile() : _snapshot?.Profile ?? CurrentProfile();
        Text("WardrobeIdentity", string.IsNullOrWhiteSpace(profile.Username) ? "更衣橱" : profile.Username, literal: true);
        var profileCaption = ProfileCaption(profile);
        Text("WardrobeProfileKind", profileCaption.Content, profileCaption.Literal);
        Text("WardrobeStatus", _status); Text("WardrobeLocalStatus", _status);
        Text("WardrobeClassic", (!_slim ? "✓  " : "") + "经典模型"); Text("WardrobeSlim", (_slim ? "✓  " : "") + "纤细模型");
        Text("WardrobeCurrentView", ViewCaption(_currentView), literal: true);
        Text("WardrobeSkinCount", $"{_skinCards.Count} 项"); Text("WardrobeCapeCount", $"{_capeCards.Count} 项");
        Text("WardrobeSkinsEmpty", _snapshot is not null
            ? "还没有发现可展示的皮肤。可以选择本地皮肤或打开皮肤库。"
            : _observedIdentity is null ? "请先添加并选择一个账户。"
            : !_loaded ? "正在读取可展示的皮肤…" : "暂时无法读取皮肤，请检查网络和授权后刷新。");
        bool capeEmpty = _capeCards.Count == 0;
        Text("WardrobeCapeTitle", profile.Kind switch { LaunchProfileKind.Microsoft => "正版账户披风", LaunchProfileKind.LittleSkin => "LittleSkin 披风库", LaunchProfileKind.ThirdParty => "第三方披风", _ => "披风" });
        Text("WardrobeCapeSubtitle", profile.Kind switch
        {
            LaunchProfileKind.Microsoft => "仅可切换当前正版账户已经获得的披风。",
            LaunchProfileKind.LittleSkin => "LittleSkin 披风衣柜，可自由切换账户衣柜中的披风。",
            LaunchProfileKind.ThirdParty => "第三方认证站的披风由对应皮肤站管理。",
            _ => "当前、历史和其他档案中发现的披风。"
        });
        var capeState = _snapshot?.CapeState ?? (!_loaded ? AccountWardrobeCapeState.Loading : AccountWardrobeCapeState.LoadFailed);
        Text("WardrobeCapeStatus", capeState switch
        {
            AccountWardrobeCapeState.Loading => "正在读取当前账户的披风…",
            AccountWardrobeCapeState.LoadFailed => "暂时无法读取账户披风，请检查网络和授权后刷新。",
            AccountWardrobeCapeState.Unsupported => _snapshot?.UnavailableReason ?? "当前账户不支持在启动器内更换披风。",
            _ => profile.Kind switch
            {
                LaunchProfileKind.Microsoft => "当前正版账户尚未获得任何披风。",
                LaunchProfileKind.LittleSkin => "LittleSkin 披风衣柜为空，可先在 LittleSkin 添加披风。",
                LaunchProfileKind.ThirdParty => "尚未发现披风；可前往对应第三方皮肤站设置。",
                _ => "当前档案和其他档案中没有发现披风。"
            }
        });
        Visible("WardrobeCapeStatus", capeEmpty); Visible("WardrobeCapes", !capeEmpty);
        Visible("WardrobeSkinsEmpty", _skinCards.Count == 0); Visible("WardrobeSkins", _skinCards.Count > 0);
        Visible("WardrobeSkinPager", _skinCards.Count > 0); Visible("WardrobeLocalLayer", _localOpen);
        bool skin = !Busy && _snapshot?.CanUploadSkin == true, cape = !Busy && _snapshot?.CanChooseCape == true;
        foreach (string name in new[] { "WardrobeSkinPath", "WardrobeClassic", "WardrobeSlim", "WardrobePreview" }) Enable(name, skin);
        bool canManage = SafeManageUri(_snapshot?.ManageUri) is not null && _openUrl is not null;
        Enable("WardrobeBrowse", !Busy && _snapshot is not null && (skin && _pickFile is not null || canManage));
        Enable("WardrobeChooseFile", skin && _pickFile is not null); Enable("WardrobeUpload", skin && _preview is not null);
        Enable("WardrobeLibrary", !Busy && _snapshot is not null && _openLibrary is not null);
        Visible("WardrobeManage", canManage); Enable("WardrobeManage", !Busy && canManage);
        Enable("WardrobeCapeClear", cape); Visible("WardrobeCapeClear", _snapshot?.CanChooseCape == true);
        Enable("WardrobeRefresh", !Busy); Enable("WardrobeCancel", Busy); Visible("WardrobeCancel", Busy && !_localOpen);
        Enable("WardrobeLocalClose", true); Enable("WardrobeCurrentView", true);
        UpdateTrackButtons("WardrobeSkin", _skinCards.Count, Scroll("WardrobeSkins").OffsetX);
        UpdateTrackButtons("WardrobeCape", _capeCards.Count, Scroll("WardrobeCapes").OffsetX);
        var current = _snapshot?.Current ?? new AccountWardrobeResolvedTextures(profile.SkinAddress, null,
            LaunchProfilePresentation.Avatar(profile.Uuid ?? "") == "pcl/avatar/alex", CurrentSkin(profile), null);
        if (_presentedCurrent != current || _presentedCurrentView != _currentView)
        {
            _shell.Tree.GetComponent<XsrUiImage>(_nodes["WardrobePlayerPreview"])!.Raster = WardrobePlayerPresentation.Player(current.Skin, current.Cape, current.IsSlim, _currentView);
            _presentedCurrent = current; _presentedCurrentView = _currentView;
        }
        var head = _shell.Tree.GetComponent<XsrUiImage>(_nodes["WardrobeHeadPreview"])!;
        head.Source = LaunchProfilePresentation.Avatar(profile.Uuid ?? "");
        head.Raster = _preview is { } selected ? LaunchProfilePresentation.Head(selected.Image) : null;
        _shell.Tree.GetComponent<XsrUiImage>(_nodes["WardrobeTexturePreview"])!.Raster = _preview is { } draft
            ? new XsrUiRasterImage(draft.Image, []) { FitToBounds = true } : null;
        foreach (var card in _skinCards.Concat(_capeCards))
        {
            var textures = card.Loaded ?? card.Card.Appearance;
            if (card.Presented != textures || card.PresentedView != card.View)
            {
                _shell.Tree.GetComponent<XsrUiImage>(card.Image)!.Raster = WardrobePlayerPresentation.Player(textures.Skin, textures.Cape, textures.IsSlim, card.View);
                var caption = _shell.Tree.GetComponent<XsrUiText>(card.ViewButton)!;
                caption.Content = ViewCaption(card.View); caption.Localize = false;
                card.Presented = textures; card.PresentedView = card.View;
            }
            if (card.Apply.IsAssigned) _shell.Tree.GetComponent<XsrUiInput>(card.Apply)!.Enabled = !Busy && !_localOpen && _snapshot is not null;
        }
        _shell.Tree.MarkDirty(Page, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        _skinOffset = Scroll("WardrobeSkins").OffsetX; _capeOffset = Scroll("WardrobeCapes").OffsetX;
    }
    private Nexa.Core.Media.PngImage? CurrentSkin(LaunchProfileView profile) => _store.ReadCollection<AccountSkinSnapshot>(_store.Resolve(AccountSkinContract.SkinsKey)).Items
        .FirstOrDefault(s => s.ProfileKey == AccountSkinContract.ProfileKey(profile))?.Image;
    private (string Content, bool Literal) ProfileCaption(LaunchProfileView profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Username)) return ("请先选择账户", false);
        if (profile.Kind == LaunchProfileKind.ThirdParty)
        {
            string server = profile.AuthServer.Trim();
            if (!server.Contains("://", StringComparison.Ordinal)) server = "https://" + server;
            if (Uri.TryCreate(server, UriKind.Absolute, out var address) && !string.IsNullOrEmpty(address.Host))
                return (string.Format(CultureInfo.CurrentCulture, _shell.Renderer.LocalizeText("第三方 · {0}"), address.Host), true);
        }
        return !string.IsNullOrWhiteSpace(profile.Info)
            ? (profile.Info, true) : (LaunchProfilePresentation.Description(profile), false);
    }
    private static string ViewLabel(WardrobePlayerView view) => view switch
    { WardrobePlayerView.Isometric => "立体", WardrobePlayerView.Front => "正面", WardrobePlayerView.Back => "背面", WardrobePlayerView.Left => "左侧", WardrobePlayerView.Right => "右侧", WardrobePlayerView.Top => "顶部", _ => "底部" };
    private string ViewCaption(WardrobePlayerView view) => string.Format(CultureInfo.CurrentCulture,
        _shell.Renderer.LocalizeText("{0} · 切换视角"), _shell.Renderer.LocalizeText(ViewLabel(view)));
    private void UpdateTrackButtons(string prefix, int count, double offset)
    {
        Enable(prefix + "Previous", !Busy && offset > 0);
        Enable(prefix + "Next", !Busy && offset + _trackWidth + .5 < count * (_cardWidth + 12) - 12);
    }
    private void BuildCards()
    {
        ClearCards();
        foreach (var card in _snapshot?.Skins ?? []) AddCard(card, _nodes["WardrobeSkins"], _skinCards);
        var capes = _snapshot?.CapeCards ?? [];
        if (capes.Count == 0 && _snapshot is { } legacy && legacy.Capes.Count > 0)
            capes = legacy.Capes.Select(cape => new AccountWardrobeCard(cape.Id, cape.Name,
                cape.IsActive ? "当前使用" : legacy.Profile.Kind == LaunchProfileKind.Microsoft ? "正版账户已获得" : "LittleSkin 衣柜",
                (legacy.Current ?? new(null, null, false, null, null)) with { CapeAddress = cape.TextureAddress, Cape = null },
                legacy.CanChooseCape && !cape.IsActive, CapeId: cape.Id)
            { Kind = AccountWardrobeTextureKind.Cape, IsActive = cape.IsActive }).ToArray();
        foreach (var card in capes) AddCard(card, _nodes["WardrobeCapes"], _capeCards);
        SetCardMetrics();
    }
    private void AddCard(AccountWardrobeCard card, XsrUiEntityId parent, List<CardVisual> cards)
    {
        string prefix = card.Kind == AccountWardrobeTextureKind.Cape ? "WardrobeCape" : "WardrobeSkin";
        var container = Element(parent, prefix + "Card." + card.Id, XsrUiOrientation.Vertical, 0);
        var border = _shell.Tree.GetComponent<XsrUiVisualStyle>(container)!; border.Background = new(255, 255, 255); border.CornerRadius = 18; border.Hover = new(242, 247, 253);
        var body = Element(container, prefix + "Body." + card.Id, XsrUiOrientation.Vertical, 8);
        _shell.Tree.GetComponent<XsrUiElement>(body)!.Weight = 1;
        var image = _shell.Tree.Create(prefix + "Preview." + card.Id); _shell.Tree.Attach(image, body);
        _shell.Tree.SetComponent(image, new XsrUiElement { Height = 140, Weight = 1 });
        _shell.Tree.SetComponent(image, new XsrUiImage("lucide/image"));
        _shell.Tree.SetComponent(image, new XsrUiSemantic(XsrUiSemanticRole.Image, card.Title) { Localize = false });
        var details = Element(body, prefix + "Details." + card.Id, XsrUiOrientation.Vertical, 3);
        var title = Label(details, prefix + "Title." + card.Id, card.Title, 25, true);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(title)!.FontWeight = 600;
        Label(details, prefix + "Source." + card.Id, card.Source, 22, false);
        var viewButton = Button(details, prefix + "View." + card.Id, "切换视角", "ui.wardrobe.view.card", 25);
        XsrUiEntityId apply = default;
        if (card.CanApply && !card.IsActive)
            apply = Button(details, card.Kind == AccountWardrobeTextureKind.Cape ? prefix + "." + card.Id : prefix + "Apply." + card.Id,
                card.Kind == AccountWardrobeTextureKind.Cape ? "使用披风" : "使用皮肤", card.Kind == AccountWardrobeTextureKind.Cape ? "ui.wardrobe.cape.choose" : "ui.wardrobe.skin.choose", 30);
        var visual = new CardVisual(card, container, body, image, details, viewButton, apply); cards.Add(visual);
        _viewSources[viewButton] = visual; if (apply.IsAssigned) _applySources[apply] = visual;
    }
    private XsrUiEntityId Element(XsrUiEntityId parent, string name, XsrUiOrientation direction, double spacing)
    {
        var entity = _shell.Tree.Create(name); _shell.Tree.Attach(entity, parent);
        _shell.Tree.SetComponent(entity, new XsrUiElement()); _shell.Tree.SetComponent(entity, new XsrUiStackPanel(direction) { Spacing = spacing }); Style(entity); return entity;
    }
    private XsrUiEntityId Label(XsrUiEntityId parent, string name, string value, double height, bool literal)
    {
        var entity = _shell.Tree.Create(name); _shell.Tree.Attach(entity, parent);
        _shell.Tree.SetComponent(entity, new XsrUiElement { Height = height });
        _shell.Tree.SetComponent(entity, new XsrUiText(value) { Localize = !literal, MaxLines = 1, TrimOverflow = true });
        _shell.Tree.SetComponent(entity, new XsrUiSemantic(XsrUiSemanticRole.Text, value) { Localize = !literal }); Style(entity);
        var style = _shell.Tree.GetComponent<XsrUiVisualStyle>(entity)!; style.FontSize = literal ? 13.5 : 11; style.TextAlignment = XsrUiTextAlignment.Center; return entity;
    }
    private XsrUiEntityId Button(XsrUiEntityId parent, string name, string label, string command, double height)
    {
        var entity = Label(parent, name, label, height, false);
        _shell.Tree.SetComponent(entity, new XsrUiInput { Clickable = true, Focusable = true });
        _shell.Tree.SetComponent(entity, new XsrUiCommandBinding(XsrSemanticId.Parse(command)));
        _shell.Tree.SetComponent(entity, new XsrUiSemantic(XsrUiSemanticRole.Button, label)); Style(entity); return entity;
    }
    private void ApplyResponsiveLayout()
    {
        if (_viewport == _shell.Renderer.Viewport) return;
        _viewport = _shell.Renderer.Viewport;
        double rail = _viewport.Width >= 1380 ? 326 : _viewport.Width <= 940 ? 268 : 300;
        bool compact = _viewport.Height <= 650;
        var railElement = _shell.Tree.GetComponent<XsrUiElement>(_nodes["WardrobeProfileRail"])!;
        railElement.Padding = compact ? new(18, 14, 18, 16) : new(22, 18, 22, 22);
        railElement.Width = rail - railElement.Padding.Horizontal;
        double edge = _viewport.Width >= 1500 ? 38 : _viewport.Width <= 980 ? 18 : 26;
        _shell.Tree.GetComponent<XsrUiElement>(_nodes["WardrobeContent"])!.Padding = new(edge, compact ? 10 : 20, edge, compact ? 10 : 24);
        _shell.Tree.GetComponent<XsrUiStackPanel>(_nodes["WardrobeContent"])!.Spacing = compact ? 4 : 8;
        foreach (string key in new[] { "WardrobeSkinHeader", "WardrobeCapeHeader" }) _shell.Tree.GetComponent<XsrUiElement>(_nodes[key])!.Height = compact ? 22 : 28;
        _shell.Tree.GetComponent<XsrUiElement>(_nodes["WardrobeCapeHeader"])!.Margin = new(0, compact ? 4 : 8, 0, 0);
        _shell.Tree.GetComponent<XsrUiElement>(_nodes["WardrobeMainHeader"])!.Height = compact ? 30 : 34;
        _shell.Tree.GetComponent<XsrUiElement>(_nodes["WardrobeStatus"])!.Height = compact ? 30 : 40;
        _shell.Tree.GetComponent<XsrUiElement>(_nodes["WardrobeSkinSubtitle"])!.Height = compact ? 22 : 28;
        _shell.Tree.GetComponent<XsrUiElement>(_nodes["WardrobeCapeSubtitle"])!.Height = compact ? 26 : 30;
        _shell.Tree.GetComponent<XsrUiElement>(_nodes["WardrobeSkinPager"])!.Height = compact ? 24 : 28;
        _shell.Tree.GetComponent<XsrUiElement>(_nodes["WardrobeCapePager"])!.Height = compact ? 26 : 30;
        _trackWidth = Math.Max(120, _viewport.Width - rail - edge * 2 - 32);
        // The shell uses 100 vertical pixels. The remaining fixed content, padding and
        // inter-row spacing use 262 compact / 370 regular pixels before the two tracks.
        double trackHeight = Math.Clamp((_viewport.Height - (compact ? 362 : 470)) / 2, 112, 280);
        _cardHeight = Math.Max(112, trackHeight - 6);
        _cardWidth = _cardHeight < 216 ? Math.Clamp((_cardHeight - 18) * .65 + 116, 164, 244) : 176;
        var localPanel = _shell.Tree.GetComponent<XsrUiElement>(_nodes["WardrobeLocalPanel"])!;
        localPanel.Width = Math.Min(600, Math.Max(260, _viewport.Width - 48)) - localPanel.Padding.Horizontal;
        localPanel.MaxHeight = Math.Min(500, Math.Max(140, _viewport.Height - 100)) - localPanel.Padding.Vertical;
        SetCardMetrics(); _dirty = true; _shell.Tree.MarkDirty(Page, XsrUiDirtyKinds.Layout);
    }
    private void SetCardMetrics()
    {
        bool horizontal = _cardHeight < 216;
        foreach (var card in _skinCards.Concat(_capeCards))
        {
            var container = _shell.Tree.GetComponent<XsrUiElement>(card.Container)!;
            container.Padding = horizontal ? new(10, 8, 10, 8) : new(14, 10, 14, 12);
            container.Width = container.MinWidth = _cardWidth - container.Padding.Horizontal;
            container.Height = _cardHeight - container.Padding.Vertical;
            _shell.Tree.GetComponent<XsrUiStackPanel>(card.Body)!.Direction = horizontal ? XsrUiOrientation.Horizontal : XsrUiOrientation.Vertical;
            var image = _shell.Tree.GetComponent<XsrUiElement>(card.Image)!;
            image.Width = horizontal ? Math.Max(48, (_cardHeight - 18) * .65) : null;
            image.Height = horizontal ? _cardHeight - 18 : Math.Max(72, _cardHeight - (card.Apply.IsAssigned ? 112 : 78));
            image.Weight = horizontal ? 0 : 1;
            var details = _shell.Tree.GetComponent<XsrUiElement>(card.Details)!; details.Weight = horizontal ? 1 : 0; details.VerticalAlignment = XsrUiAlignment.Center;
            var labels = _shell.Tree.Children(card.Details);
            _shell.Tree.GetComponent<XsrUiElement>(labels[0])!.Height = horizontal ? 20 : 25;
            _shell.Tree.GetComponent<XsrUiElement>(labels[1])!.Height = horizontal ? 18 : 22;
            _shell.Tree.GetComponent<XsrUiElement>(card.ViewButton)!.Height = horizontal ? 22 : 25;
            if (card.Apply.IsAssigned) _shell.Tree.GetComponent<XsrUiElement>(card.Apply)!.Height = horizontal ? 26 : 30;
            _shell.Tree.MarkDirty(card.Container, XsrUiDirtyKinds.Layout);
        }
    }
    private XsrUiScroll Scroll(string node) => _shell.Tree.GetComponent<XsrUiScroll>(_nodes[node])!;
    private void ClearCards()
    {
        foreach (string key in new[] { "WardrobeSkins", "WardrobeCapes" })
        {
            foreach (var child in _shell.Tree.Children(_nodes[key]).ToArray()) _shell.Tree.Destroy(child);
            Scroll(key).OffsetX = 0;
        }
        _skinCards.Clear(); _capeCards.Clear(); _applySources.Clear(); _viewSources.Clear(); _textures.Clear(); _textureAttempts.Clear();
    }
    private void Retire()
    {
        _dirty = true; _stop.Cancel(); _stop.Dispose(); _stop = new();
        _picking = null; _reading = null; _readingProfile = null; _previewing = null; _writing = null; _writingProfile = null; _snapshot = null; _preview = null;
        _loaded = false; _slim = false; _localOpen = false; _operationNotice = null; _presentedCurrent = null; _currentView = WardrobePlayerView.Front;
        while (_pending.TryDequeue(out _)) { }
        _shell.Renderer.SetTextInputValue(_nodes["WardrobeSkinPath"], "");
        foreach (string name in new[] { "WardrobeHeadPreview", "WardrobeTexturePreview", "WardrobePlayerPreview" })
            _shell.Tree.GetComponent<XsrUiImage>(_nodes[name])!.Raster = null;
        ClearCards();
    }
    private void Text(string name, string content, bool literal = false)
    {
        var entity = _nodes[name]; var text = _shell.Tree.GetComponent<XsrUiText>(entity)!;
        text.Localize = !literal;
        if (text.Content == content) return; text.Content = content;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
    private void Enable(string name, bool enabled) => _shell.Tree.GetComponent<XsrUiInput>(_nodes[name])!.Enabled = enabled;
    private void Visible(string name, bool visible) => _shell.Tree.GetComponent<XsrUiElement>(_nodes[name])!.IsVisible = visible;
    private void Style(XsrUiEntityId entity)
    {
        bool button = _shell.Tree.GetComponent<XsrUiInput>(entity)?.Clickable == true;
        _shell.Tree.SetComponent(entity, new XsrUiVisualStyle
        {
            Foreground = new(43, 51, 64),
            Background = button ? new(241, 245, 250) : XsrUiColor.Transparent,
            CornerRadius = button ? 10 : 0,
            FontSize = 14,
            WrapText = !button,
            TextAlignment = button ? XsrUiTextAlignment.Center : XsrUiTextAlignment.Start
        });
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _intents.IntentEmitted -= OnIntent; _shell.Renderer.FramePreparing -= OnFrame; _store.Changed -= OnStateChanged;
        Retire(); _stop.Dispose();
    }
}
