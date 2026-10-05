using System.Collections.Concurrent;
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

/// <summary>Render-thread wardrobe projection and typed account intent adapter.</summary>
internal sealed class WardrobePageController : IDisposable
{
    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly XsrQueryRouter _queries;
    private readonly XsrCommandRouter _commands;
    private readonly XsrStateStore _store;
    private readonly DesktopFeedbackService _feedback;
    private readonly Dictionary<string, XsrUiEntityId> _nodes = [];
    private readonly Dictionary<XsrUiEntityId, string> _capeRows = [];
    private readonly ConcurrentQueue<DesktopUiIntent> _pending = new();
    private CancellationTokenSource _stop = new();
    private Func<CancellationToken, Task<string?>>? _pickFile;
    private Task<string?>? _picking;
    private Task<XsrResult<AccountWardrobeSnapshot>>? _reading;
    private Task<XsrResult<AccountWardrobeSkinPreview>>? _previewing;
    private Task<XsrResult>? _writing;
    private AccountWardrobeSnapshot? _snapshot;
    private AccountWardrobeSkinPreview? _preview;
    private string? _observedIdentity;
    private bool _visible, _loaded, _slim, _disposed;
    private volatile bool _dirty = true;
    private long _wake, _selectionEvents, _seenSelectionEvents, _rosterEvents, _seenRosterEvents;
    private string _status = "正在读取当前账户…";
    private string? _operationNotice;
    private int _capePage;
    internal const int CapePageSize = 12;
    private bool Busy => _reading is not null || _previewing is not null || _writing is not null || _picking is not null;

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
            Style(entity); return true;
        });
        shell.Tree.SetComponent(_nodes["WardrobeRoot"], new XsrUiScrollGesture());
        _intents.IntentEmitted += OnIntent;
        shell.Renderer.FramePreparing += OnFrame;
        _store.Changed += OnStateChanged;
    }
    internal XsrUiEntityId Page { get; }
    internal void ConfigureFilePicker(Func<CancellationToken, Task<string?>> picker) => _pickFile = picker;
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
    private string? CurrentIdentity()
    {
        int selected = _store.Read<int>(_store.Resolve(AccountStateContract.SelectedKey)).Value;
        var profile = _store.ReadCollection<LaunchProfileView>(_store.Resolve(AccountStateContract.ProfilesKey)).Items
            .FirstOrDefault(p => p.Index == selected);
        return selected < 0 || string.IsNullOrWhiteSpace(profile.Username) ? null
            : $"{selected}\n{profile.Kind}\n{profile.Uuid}\n{profile.AuthServer}\n{profile.Username}";
    }
    private void OnFrame(object? sender, EventArgs args)
    {
        if (_disposed) return;
        bool visible = _shell.Stage.Navigation.Current == Page;
        if (!visible) { if (_visible) Retire(); _visible = false; return; }
        long selections = Volatile.Read(ref _selectionEvents), rosters = Volatile.Read(ref _rosterEvents);
        if (_visible && (_loaded || _observedIdentity is null) && !_dirty && _pending.IsEmpty
            && selections == _seenSelectionEvents && rosters == _seenRosterEvents
            && _picking is not { IsCompleted: true } && _reading is not { IsCompleted: true }
            && _previewing is not { IsCompleted: true } && _writing is not { IsCompleted: true }) return;
        string? identity = CurrentIdentity();
        // Auth refreshes may publish the same visible identity while this query/write owns
        // its operation. Services admits the new stamp and rejects external roster changes.
        bool rosterChangedWhileIdle = rosters != _seenRosterEvents && _reading is null && _writing is null;
        if (!_visible || identity != _observedIdentity || selections != _seenSelectionEvents || rosterChangedWhileIdle)
        { Retire(); _observedIdentity = identity; _status = identity is null ? "请先添加并选择一个账户。" : "正在读取当前账户…"; }
        _visible = true; _seenSelectionEvents = selections; _seenRosterEvents = rosters;
        if (identity is null) { Present(); return; }
        while (_pending.TryDequeue(out var intent)) Handle(intent);
        if (_picking is { IsCompleted: true } picking)
        {
            _dirty = true;
            _picking = null;
            if (picking.IsCompletedSuccessfully && picking.Result is { } path)
            { _shell.Renderer.SetTextInputValue(_nodes["WardrobeSkinPath"], path); BeginPreview(); }
            else if (picking.IsFaulted) _status = "无法选择皮肤文件，请重试。";
        }
        if (!_loaded && _reading is null) BeginRead();
        if (_reading is { IsCompleted: true } reading)
        {
            _dirty = true;
            _reading = null; _loaded = true;
            if (Succeeded(reading))
            {
                _snapshot = reading.Result.Value;
                _status = _operationNotice ?? _snapshot.UnavailableReason ?? "选择皮肤或已获得的披风。";
                BuildCapes();
            }
            else _status = reading.IsCompletedSuccessfully ? reading.Result.Error?.Message ?? "无法读取更衣橱。" : "无法读取更衣橱，请重试。";
            // A failed provider read may still have rotated credentials. Consume this
            // operation's roster events so it waits for an explicit refresh instead of
            // turning a failed read into another authentication/query loop.
            _seenRosterEvents = Volatile.Read(ref _rosterEvents);
        }
        if (_previewing is { IsCompleted: true } previewing)
        {
            _dirty = true;
            _previewing = null;
            if (Succeeded(previewing)) { _preview = previewing.Result.Value; _status = "皮肤已验证，可以上传。"; }
            else _status = previewing.IsCompletedSuccessfully ? previewing.Result.Error?.Message ?? "皮肤无法使用。" : "无法读取皮肤文件。";
        }
        if (_writing is { IsCompleted: true } writing)
        {
            _dirty = true;
            _writing = null;
            if (writing.IsCompletedSuccessfully && writing.Result.IsSuccess)
            {
                _operationNotice = null;
                _feedback.Info("外观已更新。"); _loaded = false; _snapshot = null; _preview = null;
                _shell.Renderer.SetTextInputValue(_nodes["WardrobeSkinPath"], "");
                _status = "外观已更新，正在刷新…";
                _seenRosterEvents = Volatile.Read(ref _rosterEvents);
            }
            else
            {
                _operationNotice = _status = writing.IsCompletedSuccessfully ? writing.Result.Error?.Message ?? "外观操作未完成。" : "外观操作未完成，请重试。";
                // Authentication can rotate credentials even when the following provider
                // mutation fails. Always obtain a fresh admitted stamp before a retry.
                _loaded = false; _snapshot = null;
                _seenRosterEvents = Volatile.Read(ref _rosterEvents);
            }
        }
        Present();
    }
    private void Handle(DesktopUiIntent intent)
    {
        _dirty = true;
        string command = intent.Command.Value;
        if (command == "ui.wardrobe.cancel") { Retire(); _observedIdentity = CurrentIdentity(); _loaded = true; _status = "操作已取消。点击刷新重新读取外观。"; return; }
        if (command == "ui.wardrobe.refresh") { if (!Busy) { Retire(); _observedIdentity = CurrentIdentity(); } return; }
        if (Busy || _snapshot is not { } snapshot) return;
        _operationNotice = null;
        if (command is "ui.wardrobe.cape.previous" or "ui.wardrobe.cape.next")
        {
            _capePage = Math.Clamp(_capePage + (command.EndsWith("next", StringComparison.Ordinal) ? 1 : -1), 0,
                Math.Max(0, (snapshot.Capes.Count - 1) / CapePageSize)); BuildCapes();
        }
        else if (command is "ui.wardrobe.classic" or "ui.wardrobe.slim")
        { _slim = command.EndsWith("slim", StringComparison.Ordinal); _preview = null; _status = "模型已改变，请重新校验皮肤。"; }
        else if (command == "ui.wardrobe.browse" && snapshot.CanUploadSkin && _pickFile is not null)
        { _picking = _pickFile(_stop.Token); Wake(_picking); }
        else if (command == "ui.wardrobe.preview" && snapshot.CanUploadSkin) BeginPreview();
        else if (command == "ui.wardrobe.upload" && snapshot.CanUploadSkin && _preview is { } preview)
            Write(AccountWardrobeContract.UploadSkin, new AccountWardrobeUploadSkinCommand(snapshot.Identity,
                preview.Image.Bytes.ToArray(), preview.FileName, preview.IsSlim));
        else if (command == "ui.wardrobe.cape.clear" && snapshot.CanChooseCape)
            Write(AccountWardrobeContract.SetCape, new AccountWardrobeSetCapeCommand(snapshot.Identity, null));
        else if (command == "ui.wardrobe.cape.choose" && snapshot.CanChooseCape && _capeRows.TryGetValue(intent.Source, out string? id))
            Write(AccountWardrobeContract.SetCape, new AccountWardrobeSetCapeCommand(snapshot.Identity, id));
    }
    private void BeginRead()
    {
        _dirty = true;
        _status = _operationNotice ?? "正在读取当前账户…";
        if (!_queries.TryResolve(AccountWardrobeContract.Read, out var route)) { _loaded = true; _status = "更衣橱服务不可用。"; return; }
        _reading = _queries.QueryAsync<AccountWardrobeQuery, AccountWardrobeSnapshot>(route, new(), cancellationToken: _stop.Token).AsTask(); Wake(_reading);
    }
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
        _writing = _commands.Dispatch(route, command, cancellationToken: _stop.Token).Completion;
        Wake(_writing); _status = "正在更新外观…";
    }
    private void Wake(Task task)
    {
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception; // Retired requests still have their failures observed.
            if (_disposed) return;
            _dirty = true;
            try { _store.Publish(_store.Resolve(WardrobePresentationState.WakeKey), Interlocked.Increment(ref _wake)); }
            catch (ObjectDisposedException) { }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private static bool Succeeded<T>(Task<XsrResult<T>> task) => task.IsCompletedSuccessfully && task.Result.IsSuccess;
    private void Present()
    {
        if (!_dirty) return; _dirty = false;
        Text("WardrobeIdentity", _snapshot is { } snapshot ? snapshot.Profile.Username + " · 更衣橱" : "更衣橱");
        Text("WardrobeStatus", _status);
        Text("WardrobeClassic", (!_slim ? "✓  " : "") + "经典模型"); Text("WardrobeSlim", (_slim ? "✓  " : "") + "纤细模型");
        Text("WardrobeCapeStatus", _snapshot?.Capes.Count is > 0
            ? $"{_capePage + 1} / {(_snapshot.Capes.Count + CapePageSize - 1) / CapePageSize} · {_snapshot.Capes.Count} 个披风"
            : "当前账户没有可选择的披风。");
        bool skin = !Busy && _snapshot?.CanUploadSkin == true, cape = !Busy && _snapshot?.CanChooseCape == true;
        foreach (string name in new[] { "WardrobeSkinPath", "WardrobeClassic", "WardrobeSlim", "WardrobePreview" }) Enable(name, skin);
        Enable("WardrobeBrowse", skin && _pickFile is not null); Enable("WardrobeUpload", skin && _preview is not null);
        Enable("WardrobeCapeClear", cape); Enable("WardrobeRefresh", !Busy); Enable("WardrobeCancel", Busy);
        Enable("WardrobeCapePrevious", !Busy && _capePage > 0);
        Enable("WardrobeCapeNext", !Busy && (_capePage + 1) * CapePageSize < (_snapshot?.Capes.Count ?? 0));
        foreach (var entity in _capeRows.Keys) _shell.Tree.GetComponent<XsrUiInput>(entity)!.Enabled = cape;
        var head = _shell.Tree.GetComponent<XsrUiImage>(_nodes["WardrobeHeadPreview"])!;
        var selectedImage = _preview?.Image;
        if (selectedImage is null && _snapshot is { } current)
            selectedImage = _store.ReadCollection<AccountSkinSnapshot>(_store.Resolve(AccountSkinContract.SkinsKey)).Items
                .FirstOrDefault(s => s.ProfileKey == AccountSkinContract.ProfileKey(current.Profile))?.Image;
        head.Source = _snapshot is { } profile ? LaunchProfilePresentation.Avatar(profile.Profile.Uuid) : "pcl/avatar/steve";
        head.Raster = selectedImage is null ? null : LaunchProfilePresentation.Head(selectedImage);
        _shell.Tree.GetComponent<XsrUiImage>(_nodes["WardrobeTexturePreview"])!.Raster = _preview is { } draft
            ? new XsrUiRasterImage(draft.Image, []) { FitToBounds = true } : null;
        _shell.Tree.MarkDirty(Page, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
    private void BuildCapes()
    {
        var parent = _nodes["WardrobeCapes"];
        foreach (var child in _shell.Tree.Children(parent).ToArray()) _shell.Tree.Destroy(child);
        _capeRows.Clear();
        foreach (var cape in (_snapshot?.Capes ?? []).Skip(_capePage * CapePageSize).Take(CapePageSize))
        {
            var row = _shell.Tree.Create("WardrobeCape." + cape.Id);
            _shell.Tree.Attach(row, parent);
            _shell.Tree.SetComponent(row, new XsrUiElement { Height = 36, Padding = new(12, 0, 12, 0) });
            _shell.Tree.SetComponent(row, new XsrUiText((cape.IsActive ? "✓  " : "") + cape.Name) { Localize = false });
            _shell.Tree.SetComponent(row, new XsrUiSemantic(XsrUiSemanticRole.Button, cape.Name) { Localize = false });
            _shell.Tree.SetComponent(row, new XsrUiInput { Clickable = true, Focusable = true });
            _shell.Tree.SetComponent(row, new XsrUiCommandBinding(XsrSemanticId.Parse("ui.wardrobe.cape.choose")));
            _capeRows[row] = cape.Id; Style(row);
        }
    }
    private void Retire()
    {
        _dirty = true;
        _stop.Cancel(); _stop.Dispose(); _stop = new();
        _picking = null; _reading = null; _previewing = null; _writing = null; _snapshot = null; _preview = null; _loaded = false; _slim = false;
        _operationNotice = null;
        _capePage = 0;
        while (_pending.TryDequeue(out _)) { }
        _shell.Renderer.SetTextInputValue(_nodes["WardrobeSkinPath"], "");
        _shell.Tree.GetComponent<XsrUiImage>(_nodes["WardrobeHeadPreview"])!.Raster = null;
        _shell.Tree.GetComponent<XsrUiImage>(_nodes["WardrobeTexturePreview"])!.Raster = null;
        BuildCapes();
    }
    private void Text(string name, string content)
    {
        var entity = _nodes[name]; var text = _shell.Tree.GetComponent<XsrUiText>(entity)!;
        if (text.Content == content) return; text.Content = content;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
    private void Enable(string name, bool enabled) => _shell.Tree.GetComponent<XsrUiInput>(_nodes[name])!.Enabled = enabled;
    private void Style(XsrUiEntityId entity)
    {
        bool button = _shell.Tree.GetComponent<XsrUiInput>(entity)?.Clickable == true;
        _shell.Tree.SetComponent(entity, new XsrUiVisualStyle
        {
            Foreground = new(43, 51, 64),
            Background = button ? new(241, 245, 250) : XsrUiColor.Transparent,
            CornerRadius = button ? 8 : 0,
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
