using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId LaunchProfileAction = XsrSemanticId.Parse("ui.settings.launch-profile.action");
    private readonly Dictionary<XsrUiEntityId, Action> _launchProfileActions = [];
    private SettingsLaunchProfilesSnapshot? _launchProfiles;
    private Task<XsrResult<SettingsLaunchProfilesSnapshot>>? _launchProfilesReading;
    private string? _launchProfilesInstance;
    private string? _launchProfilesError, _launchProfileInputScope;
    private readonly Dictionary<string, string> _launchProfileDrafts = [];
    private readonly Dictionary<string, (XsrUiEntityId Entity, string Original)> _launchProfileDraftControls = [];
    private XsrUiEntityId _launchProfilesGroup, _launchProfileName, _launchMods, _launchResourcePacks, _launchShaders, _launchConfig;

    private void ResetLaunchProfileControls()
    {
        _launchProfilesGroup = _launchProfileName = _launchMods = _launchResourcePacks = _launchShaders = _launchConfig = default;
        _launchProfileActions.Clear(); _launchProfileDraftControls.Clear();
    }

    private void CaptureLaunchProfileDrafts(bool navigating = false)
    {
        if (navigating) { _launchProfileDrafts.Clear(); _launchProfileInputScope = null; return; }
        foreach (var control in _launchProfileDraftControls)
        {
            if (!_shell.Tree.IsAlive(control.Value.Entity)) continue;
            string value = _shell.Tree.GetComponent<XsrUiTextInput>(control.Value.Entity)!.ReadDraft();
            if (value == control.Value.Original) _launchProfileDrafts.Remove(control.Key);
            else _launchProfileDrafts[control.Key] = value;
        }
    }

    private void BuildLaunchProfiles()
    {
        if (_instance is null || _selected != "game") return;
        _launchProfilesGroup = FormGroup(_sections, "LaunchProfiles", "启动配置与临时覆盖");
        BuildLaunchProfileControls();
    }

    private void BuildLaunchProfileControls()
    {
        if (!_shell.Tree.IsAlive(_launchProfilesGroup)) return;
        string? focus = _shell.Tree.IsAlive(_shell.Renderer.Focused)
            && _launchProfileDraftControls.Values.Any(control => control.Entity == _shell.Renderer.Focused)
            ? _shell.Tree.Name(_shell.Renderer.Focused) : null;
        CaptureLaunchProfileDrafts();
        foreach (var child in _shell.Tree.Children(_launchProfilesGroup).ToArray()) _shell.Tree.Destroy(child);
        _launchProfileActions.Clear(); _launchProfileDraftControls.Clear();
        if (_launchProfilesError is { } error)
        {
            Text(_launchProfilesGroup, error, 12, Muted, 28);
            Add(_launchProfilesGroup, "LaunchProfilesRetry", "重新读取启动配置", () => { _launchProfilesError = null; _launchProfiles = null; });
            return;
        }
        if (_launchProfiles is null) { Text(_launchProfilesGroup, "正在读取启动配置…", 12, Muted, 24); return; }
        string scope = _instance + "\0" + _launchProfiles.SelectedProfileId + "\0" + _launchProfiles.TemporaryId;
        if (_launchProfileInputScope != scope) _launchProfileDrafts.Clear();
        _launchProfileInputScope = scope;
        var selected = _launchProfiles.Profiles.FirstOrDefault(profile => profile.Id == _launchProfiles.SelectedProfileId);
        string layer = _launchProfiles.TemporaryId is not null ? "当前编辑：临时覆盖（结束后撤销）"
            : selected is not null ? "当前编辑：命名配置 " + selected.Name : "当前编辑：实例设置";
        Text(_launchProfilesGroup, layer, 12, Blue, 24);
        Text(_launchProfilesGroup, "下方普通设置直接编辑当前层；Safe Launch 会暂时移走模组、资源包、光影和配置，游戏退出后恢复。", 12, Muted, 24);
        var safePolicy = FormGroup(_launchProfilesGroup, "SafeLaunchPolicy", "安全启动固定策略");
        ManagementFactIn(safePolicy, "光影", "禁用全部光影；退出后恢复原目录");
        ManagementFactIn(safePolicy, "资源包", "禁用全部资源包；退出后恢复原目录");
        ManagementFactIn(safePolicy, "启动钩子", "跳过 Wrapper、启动前及退出后命令");
        ManagementFactIn(safePolicy, "自定义 JVM 参数", "跳过自定义 JVM、游戏参数、环境变量和前置 classpath");
        Text(safePolicy, "仅在安全启动开启时生效；账户认证、Java 兼容和完整性检查始终执行。模组与配置全量隔离。", 12, Muted, 34);
        var selections = Stack(_launchProfilesGroup, "LaunchProfileChoices", XsrUiOrientation.Vertical, 4);
        Add(selections, "LaunchProfileBase", "使用实例设置", () => SelectLaunchProfile(null));
        foreach (var profile in _launchProfiles.Profiles)
        {
            var row = Stack(selections, "LaunchProfileChoice." + profile.Id, XsrUiOrientation.Horizontal, 6);
            Add(row, "LaunchProfileSelect." + profile.Id, profile.Name + (profile.Id == selected?.Id ? " · 已选择" : ""), () => SelectLaunchProfile(profile.Id));
            Add(row, "LaunchProfileDelete." + profile.Id, "删除配置", () => WriteLaunchProfile(SettingsLaunchProfileContract.Delete,
                new SettingsLaunchProfileDeleteCommand(_instance!, profile.Id, _launchProfiles!.Revision)));
        }
        _launchProfileName = Input("LaunchProfileName", "配置名称", selected?.Name ?? "新启动配置");
        MinecraftLaunchOverlay overlay = _launchProfiles.TemporaryId is not null ? _launchProfiles.TemporaryOverlay : selected?.Overlay ?? new();
        _launchMods = Input("LaunchOverlayMods", "临时模组目录（完整路径；空白不覆盖）", overlay.ModsSource ?? "");
        _launchResourcePacks = Input("LaunchOverlayResourcePacks", "临时资源包目录（完整路径）", overlay.ResourcePacksSource ?? "");
        _launchShaders = Input("LaunchOverlayShaders", "临时光影目录（完整路径）", overlay.ShaderPacksSource ?? "");
        _launchConfig = Input("LaunchOverlayConfig", "临时配置目录（完整路径）", overlay.ConfigSource ?? "");
        var actions = Stack(_launchProfilesGroup, "LaunchProfileActions", XsrUiOrientation.Horizontal, 6);
        Add(actions, "LaunchProfileCreate", "保存为新配置", () => SaveLaunchProfile(Guid.NewGuid().ToString("N")));
        if (selected is not null) Add(actions, "LaunchProfileSave", "保存名称与覆盖", () => SaveLaunchProfile(selected.Id));
        if (_launchProfiles.TemporaryId is null)
            Add(actions, "LaunchTemporaryBegin", "启用临时覆盖", () => WriteLaunchProfile(SettingsLaunchProfileContract.BeginTemporary,
                new SettingsTemporaryLaunchBeginCommand(_instance!, Guid.NewGuid().ToString("N"), new Dictionary<string, SettingsOverride>(), ReadOverlay(), _launchProfiles!.Revision)));
        else
        {
            Add(actions, "LaunchTemporarySave", "保存临时目录", () => WriteLaunchProfile(SettingsLaunchProfileContract.BeginTemporary,
                new SettingsTemporaryLaunchBeginCommand(_instance!, _launchProfiles.TemporaryId!, _launchProfiles.TemporaryValues, ReadOverlay(), _launchProfiles.Revision)));
            Add(actions, "LaunchTemporaryEnd", "撤销临时覆盖", () => WriteLaunchProfile(SettingsLaunchProfileContract.EndTemporary,
                new SettingsTemporaryLaunchEndCommand(_instance!, _launchProfiles.Revision)));
        }
        UpdateLaunchProfileAdmission();
        if (focus is not null)
            _shell.Tree.Walk(_launchProfilesGroup, entity =>
            { if (_shell.Tree.Name(entity) == focus) _shell.Renderer.Focus(entity, showIndicator: false); return true; });
        _shell.Tree.MarkDirty(_launchProfilesGroup, XsrUiDirtyKinds.Layout);

        XsrUiEntityId Input(string name, string label, string value)
        {
            Text(_launchProfilesGroup, label, 12, Muted, 22);
            var input = Element(_launchProfilesGroup, name, XsrUiSemanticRole.TextInput, label, height: 30);
            _shell.Tree.SetComponent(input, new XsrUiTextInput { MaximumLength = 4096, Placeholder = label });
            _shell.Tree.SetComponent(input, new XsrUiInput { Focusable = true, Clickable = true });
            _shell.Tree.GetComponent<XsrUiElement>(input)!.Padding = new(8, 0, 8, 0);
            Style(input, new(244, 247, 251), Ink, 6);
            string draft = _launchProfileDrafts.GetValueOrDefault(name, value);
            if (draft == value) _launchProfileDrafts.Remove(name);
            _launchProfileDraftControls[name] = (input, value);
            _shell.Renderer.SetTextInputValue(input, draft); return input;
        }
        void Add(XsrUiEntityId parent, string name, string label, Action action)
        {
            var button = ActionButton(parent, name, label, LaunchProfileAction, Math.Max(112, label.Length * 13 + 20));
            _launchProfileActions[button] = action;
        }
    }

    private bool LaunchProfileSnapshotReady => _instance is not null && _launchProfilesInstance == _instance
        && _selected == "game" && _launchProfilesReading is null && _launchProfilesError is null
        && _launchProfiles is { } profiles && profiles.Revision == _store.Read<long>(_revisionId).Value
        && _values?.Revision == profiles.Revision;

    private bool LaunchProfileWriteReady => _writing is null && LaunchProfileSnapshotReady;

    private bool LaunchProfileActionReady(XsrUiEntityId source) => _shell.Tree.IsAlive(source)
        && (LaunchProfileWriteReady || _shell.Tree.Name(source) == "LaunchProfilesRetry" && _writing is null);

    private void UpdateLaunchProfileAdmission()
    {
        foreach (var source in _launchProfileActions.Keys)
            if (_shell.Tree.GetComponent<XsrUiInput>(source) is { } input)
                input.Enabled = LaunchProfileActionReady(source);
    }

    private void HandleLaunchProfileIntent(DesktopUiIntent intent)
    {
        if (LaunchProfileActionReady(intent.Source)
            && _shell.Tree.GetComponent<XsrUiInput>(intent.Source)?.Enabled == true
            && _launchProfileActions.TryGetValue(intent.Source, out var action)) action();
    }

    private MinecraftLaunchOverlay ReadOverlay()
    {
        string? Source(XsrUiEntityId input)
        {
            string value = _shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        // Safe Launch stays a normal editable setting rather than an independent hidden toggle.
        return new(false, Source(_launchMods), Source(_launchResourcePacks), Source(_launchShaders), Source(_launchConfig));
    }

    private void SaveLaunchProfile(string id)
    {
        if (_values is null || _instance is null || _launchProfiles is null) return;
        var values = _values.Values.Where(value => SettingsPolicySchema.ByKey[value.Key].InstanceOverride && value.Key != "java.compatibility")
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
        WriteLaunchProfile(SettingsLaunchProfileContract.Save, new SettingsLaunchProfileSaveCommand(_instance, id,
            _shell.Tree.GetComponent<XsrUiTextInput>(_launchProfileName)!.ReadDraft(), values, ReadOverlay(), _launchProfiles.Revision));
    }

    private void SelectLaunchProfile(string? id)
    {
        if (_instance is not null && _launchProfiles is not null)
            WriteLaunchProfile(SettingsLaunchProfileContract.Select, new SettingsLaunchProfileSelectCommand(_instance, id, _launchProfiles.Revision));
    }

    private void WriteLaunchProfile<T>(XsrSemanticId command, T payload) where T : notnull
    {
        if (!LaunchProfileWriteReady || !_commands.TryResolve(command, out var route)) return;
        _writing = FinishAsync();
        WakeOnPlatformCompletion(_writing);
        async Task<XsrResult> FinishAsync()
        {
            var result = await _commands.Dispatch(route, payload).Completion.ConfigureAwait(false);
            if (!_disposed && !result.IsSuccess) _feedback.Error("启动配置未保存：" + result.Error?.Message);
            return result;
        }
    }

    private void UpdateLaunchProfiles()
    {
        if (_instance is null || _selected != "game") return;
        if (_launchProfilesInstance != _instance)
        { _launchProfiles = null; _launchProfilesReading = null; _launchProfilesError = null; _launchProfilesInstance = _instance; }
        long revision = _store.Read<long>(_revisionId).Value;
        if (_launchProfilesReading is null && _launchProfiles?.Revision != revision && _launchProfilesError is null)
        {
            if (!_queries.TryResolve(SettingsLaunchProfileContract.Query, out var route))
            { _launchProfilesError = "启动配置服务路由不可用。"; BuildLaunchProfileControls(); return; }
            _launchProfilesReading = _queries.QueryAsync<SettingsLaunchProfilesQuery, SettingsLaunchProfilesSnapshot>(route, new(_instance), cancellationToken: _updateStop.Token).AsTask();
            WakeOnPlatformCompletion(_launchProfilesReading);
        }
        if (_launchProfilesReading is not { IsCompleted: true } reading)
        { UpdateLaunchProfileAdmission(); return; }
        _launchProfilesReading = null;
        if (PendingQuery.Succeeded(reading)) _launchProfiles = reading.Result.Value;
        else _launchProfilesError = "无法读取启动配置，请重试。";
        BuildLaunchProfileControls();
    }

    private async Task<XsrResult> SaveLaunchLayerAsync(SettingsMutation mutation)
    {
        if (_queries.TryResolve(SettingsLaunchProfileContract.Query, out _) && !LaunchProfileSnapshotReady)
            return XsrResult.Failure(new(XsrErrorKind.Rejected, XsrSemanticId.Parse("settings.profile.refresh-pending"),
                "启动配置正在更新，请稍后重试。"));
        if (_launchProfiles?.TemporaryId is { } temporary && _instance is not null
            && _commands.TryResolve(SettingsLaunchProfileContract.BeginTemporary, out var temporaryRoute))
        {
            var values = new Dictionary<string, SettingsOverride>(_launchProfiles.TemporaryValues, StringComparer.Ordinal);
            if (mutation.Value.Mode == SettingsOverrideMode.Inherit) values.Remove(mutation.Key); else values[mutation.Key] = mutation.Value;
            return await _commands.Dispatch(temporaryRoute, new SettingsTemporaryLaunchBeginCommand(_instance, temporary, values,
                _launchProfiles.TemporaryOverlay, _launchProfiles.Revision)).Completion.ConfigureAwait(false);
        }
        if (_launchProfiles?.SelectedProfileId is { } profile)
            mutation = mutation with { Layer = SettingsLayer.Profile, ProfileId = profile };
        if (!_commands.TryResolve(SettingsPolicyContract.SetCommand, out var route))
            return XsrResult.Failure(new(XsrErrorKind.Rejected, XsrSemanticId.Parse("settings.profile.unavailable"), "Settings route is unavailable."));
        return await _commands.Dispatch(route, mutation).Completion.ConfigureAwait(false);
    }
}
