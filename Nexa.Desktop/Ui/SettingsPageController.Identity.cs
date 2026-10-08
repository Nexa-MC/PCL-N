using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId IdentityAction = XsrSemanticId.Parse("ui.settings.instance-identity.action");
    private readonly Dictionary<XsrUiEntityId, Action> _identityActions = [];
    private readonly Dictionary<string, (XsrUiEntityId Input, string Original, string Presented)> _identityInputs = new(StringComparer.Ordinal);
    private InstanceIdentitySnapshot? _identity;
    private Task<XsrResult<InstanceIdentitySnapshot>>? _identityRead;
    private Task<XsrResult>? _identityWrite;
    private string? _identityInstance;
    private XsrUiEntityId _identityGroup;
    private bool _identityStarred, _identityOfflineAllowed, _identityIsolation;
    internal Action<string>? LaunchManagementInstance { get; set; }
    internal Action<string>? ModifyManagementInstance { get; set; }
    private bool _identityFailed;
    private CancellationTokenSource? _identityStop;
    private readonly List<string> _identityNotes = [];
    private readonly List<(XsrUiEntityId Input, int Index)> _identityNoteInputs = [];
    private XsrUiEntityId _identityNotesGroup;
    private int _identityNotesPage;
    private sealed record IdentityDraftSnapshot(string Instance, string Revision, string Section,
        Dictionary<string, string> Values, string[] Notes, int NotesPage, bool Starred, bool Isolation, bool OfflineAllowed);

    private IdentityDraftSnapshot? CaptureIdentityDraft(bool navigating)
    {
        if (navigating || _identity is null || _instance is null || _identityInstance != _instance
            || _selected is not ("overview" or "servers") || !_shell.Tree.IsAlive(_identityGroup)) return null;
        CaptureIdentityNotes();
        var values = _identityInputs.Where(pair => _shell.Tree.IsAlive(pair.Value.Input)).ToDictionary(pair => pair.Key, pair =>
        {
            string draft = _shell.Tree.GetComponent<XsrUiTextInput>(pair.Value.Input)!.ReadDraft();
            return draft == pair.Value.Presented ? pair.Value.Original : draft;
        }, StringComparer.Ordinal);
        return new(_instance, _identity.Revision, _selected, values, _identityNotes.ToArray(), _identityNotesPage,
            _identityStarred, _identityIsolation, _identityOfflineAllowed);
    }

    private void RestoreIdentityDraft(IdentityDraftSnapshot? draft, string? focus)
    {
        if (draft is null || _identity is null || draft.Instance != _instance || draft.Revision != _identity.Revision
            || draft.Section != _selected || !_shell.Tree.IsAlive(_identityGroup)) return;
        foreach (var pair in draft.Values)
        {
            if (!_identityInputs.TryGetValue(pair.Key, out var control)) continue;
            _shell.Renderer.SetTextInputValue(control.Input, pair.Value);
            _identityInputs[pair.Key] = (control.Input, pair.Value, _shell.Tree.GetComponent<XsrUiTextInput>(control.Input)!.ReadDraft());
        }
        if (_selected == "overview")
        {
            _identityStarred = draft.Starred; _identityIsolation = draft.Isolation;
            SetButton("InstanceIdentityStar", _identityStarred ? "已收藏 · 点击取消" : "收藏此实例");
            SetButton("InstanceIdentityIsolation", _identityIsolation ? "游戏目录：此实例（点击改为共享）" : "游戏目录：共享根目录（点击改为隔离）");
            _identityNotes.Clear(); _identityNotes.AddRange(draft.Notes); _identityNotesPage = draft.NotesPage;
            if (_shell.Tree.IsAlive(_identityNotesGroup)) BuildIdentityNoteRows();
        }
        else
        {
            _identityOfflineAllowed = draft.OfflineAllowed;
            SetButton("InstanceOfflinePolicy", _identityOfflineAllowed ? "允许离线档案 · 点击收紧" : "要求在线账户 · 点击允许离线");
        }
        if (focus is not null) _shell.Tree.Walk(_identityGroup, entity =>
        { if (_shell.Tree.Name(entity) == focus) _shell.Renderer.Focus(entity, showIndicator: false); return true; });
    }

    private void ResetIdentityControls()
    {
        _identityGroup = _identityNotesGroup = default;
        _identityActions.Clear(); _identityInputs.Clear(); _identityNoteInputs.Clear();
    }

    private void BuildInstanceIdentity()
    {
        if (_instance is null || _selected is not ("overview" or "servers")) return;
        _identityGroup = FormGroup(_sections, "InstanceIdentity", _selected == "overview" ? "实例信息" : "服务器要求与环境匹配");
        BuildIdentityControls();
    }

    private void BuildIdentityControls()
    {
        if (!_shell.Tree.IsAlive(_identityGroup)) return;
        foreach (var child in _shell.Tree.Children(_identityGroup).ToArray()) _shell.Tree.Destroy(child);
        _identityActions.Clear(); _identityInputs.Clear(); _identityNoteInputs.Clear();
        if (_identity is null)
        {
            Text(_identityGroup, _identityFailed ? "无法读取实例信息；现有文件已保留。" : "正在读取实例信息…", 12, Muted, 24);
            if (_identityFailed) Add(_identityGroup, "InstanceIdentityRetry", "重新读取", () => { _identityFailed = false; StartIdentityRead(); BuildIdentityControls(); });
            return;
        }
        if (_selected == "overview")
        {
            ManagementFactIn(_identityGroup, "加载器", _identity.Loader);
            ManagementFactIn(_identityGroup, "实例记录的 Java 偏好", _identity.JavaPreference);
            ManagementFactIn(_identityGroup, "已记录启动次数", _identity.LaunchCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            BuildObservedInstanceHistory(_identityGroup);
            if (LaunchManagementInstance is not null) Add(_identityGroup, "InstanceOverviewLaunch", "启动此实例", () => LaunchManagementInstance(_instance!));
            if (ModifyManagementInstance is not null) Add(_identityGroup, "InstanceOverviewModify", "修改版本与目录名", () => ModifyManagementInstance(_instance!));
            ManagementFactIn(_identityGroup, "稳定身份", _identity.Identity.Length == 0 ? "首次保存时生成" : _identity.Identity);
            Input("name", "显示名称", _identity.Fields.DisplayName, 256);
            Input("description", "描述", _identity.Fields.Description, 8192);
            Input("icon", "本地图标 PNG 完整路径", _identity.Fields.IconPath, 4096);
            if (_identity.Icon is { } icon)
            {
                var preview = Element(_identityGroup, "InstanceIdentityIconPreview", XsrUiSemanticRole.Image, "实例图标", 64, 64);
                _shell.Tree.SetComponent(preview, new XsrUiRasterImage(icon, []) { FitToBounds = true, AspectRatio = (double)icon.Width / icon.Height });
            }
            if (_identity.IconNotice.Length > 0) Text(_identityGroup, _identity.IconNotice, 12, Muted, 26);
            Input("tags", "标签（逗号分隔）", string.Join(", ", _identity.Fields.Tags), 2048);
            Input("group", "分组", _identity.Fields.Group, 128);
            Input("custom-info", "启动版本描述", _identity.Fields.CustomInfo, 512);
            Input("modpack-project", "整合包项目 ID", _identity.Fields.ModpackProject, 256);
            Input("modpack-version", "整合包版本", _identity.Fields.ModpackVersion, 128);
            _identityIsolation = _identity.Fields.InstanceIsolation;
            Add(_identityGroup, "InstanceIdentityIsolation", _identityIsolation ? "游戏目录：此实例（点击改为共享）" : "游戏目录：共享根目录（点击改为隔离）", () =>
            {
                _identityIsolation = !_identityIsolation;
                SetButton("InstanceIdentityIsolation", _identityIsolation ? "游戏目录：此实例（点击改为共享）" : "游戏目录：共享根目录（点击改为隔离）");
            });
            _identityStarred = _identity.Fields.Starred;
            Add(_identityGroup, "InstanceIdentityStar", _identityStarred ? "已收藏 · 点击取消" : "收藏此实例", () =>
            { _identityStarred = !_identityStarred; SetButton("InstanceIdentityStar", _identityStarred ? "已收藏 · 点击取消" : "收藏此实例"); });
            _identityNotes.Clear(); _identityNotes.AddRange(_identity.Fields.Notes.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
            _identityNotesPage = 0;
            _identityNotesGroup = FormGroup(_identityGroup, "InstanceNotes", "备注"); BuildIdentityNoteRows();
        }
        else
        {
            ManagementFactIn(_identityGroup, "认证配置", _identity.AuthenticationLocked ? "已锁定" : "可编辑");
            Input("login", "登录要求：0 不限 / 1 Microsoft / 2 第三方 / 3 两者之一", _identity.Server.LoginRequirement.ToString(System.Globalization.CultureInfo.InvariantCulture), 1, _identity.AuthenticationLocked);
            Input("auth", "认证服务器 HTTPS 地址", _identity.Server.AuthServerAddress, 2048, _identity.AuthenticationLocked);
            Input("register", "注册页面 HTTPS 地址", _identity.Server.AuthRegisterAddress, 2048, _identity.AuthenticationLocked);
            Input("auth-name", "认证服务器名称", _identity.Server.AuthServerDisplayName, 256, _identity.AuthenticationLocked);
            Input("default-server", "默认服务器", _identity.Server.DefaultServer, 512);
            Input("game-version", "服务器要求的 Minecraft 版本（空白不约束）", _identity.Server.ExpectedGameVersion, 64);
            Input("loader", "服务器要求的加载器（如 Fabric；空白不约束）", _identity.Server.ExpectedLoader, 64);
            Input("mods", "服务器必需模组 ID（逗号分隔）", string.Join(", ", _identity.Server.RequiredMods), 16384);
            _identityOfflineAllowed = _identity.Server.OfflineLaunchAllowed;
            Add(_identityGroup, "InstanceOfflinePolicy", _identityOfflineAllowed ? "允许离线档案 · 点击收紧" : "要求在线账户 · 点击允许离线", () =>
            { _identityOfflineAllowed = !_identityOfflineAllowed; SetButton("InstanceOfflinePolicy", _identityOfflineAllowed ? "允许离线档案 · 点击收紧" : "要求在线账户 · 点击允许离线"); });
            Text(_identityGroup, !_identity.EnvironmentRequirementsDeclared ? "未声明服务器环境要求。"
                : _identity.EnvironmentMismatches.Count == 0 ? "已声明的服务器环境要求与当前实例一致。" : "服务器环境不匹配：", 12, Blue, 26);
            foreach (string mismatch in _identity.EnvironmentMismatches.Take(128)) Text(_identityGroup, mismatch, 12, Muted, 26);
            Text(_identityGroup, "离线档案准入与 Java 兼容、文件完整性和账户认证分别执行，后者始终强制。", 12, Muted, 26);
        }
        var actions = Stack(_identityGroup, "InstanceIdentityActions", XsrUiOrientation.Horizontal, 6);
        Add(actions, "InstanceIdentitySave", "保存更改", SaveIdentity);
        Add(actions, "InstanceIdentityReload", "重新读取", () => { _identity = null; _identityRead = null; StartIdentityRead(); BuildIdentityControls(); });
        _shell.Tree.MarkDirty(_identityGroup, XsrUiDirtyKinds.Layout);

        void Input(string key, string label, string value, int maximum, bool readOnly = false)
        {
            Text(_identityGroup, label, 12, Muted, 24);
            var input = Element(_identityGroup, "InstanceIdentityInput." + key, XsrUiSemanticRole.TextInput, label, height: 30);
            _shell.Tree.SetComponent(input, new XsrUiTextInput { MaximumLength = maximum, PreserveTabs = true });
            _shell.Tree.SetComponent(input, new XsrUiInput { Clickable = !readOnly, Focusable = !readOnly, Enabled = !readOnly });
            _shell.Tree.GetComponent<XsrUiElement>(input)!.Padding = new(8, 0, 8, 0);
            Style(input, new(244, 247, 251), Ink, 6); _shell.Renderer.SetTextInputValue(input, value);
            _identityInputs[key] = (input, value, _shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft());
        }
    }

    private void Add(XsrUiEntityId parent, string name, string label, Action action)
    {
        var button = ActionButton(parent, name, label, IdentityAction, Math.Max(112, label.Length * 13 + 20));
        _identityActions[button] = action;
    }

    private void SetButton(string name, string label)
    {
        foreach (var entity in _identityActions.Keys)
            if (_shell.Tree.Name(entity) == name)
            {
                _shell.Tree.GetComponent<XsrUiText>(entity)!.Content = label;
                _shell.Tree.GetComponent<XsrUiSemantic>(entity)!.Label = label;
                _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
            }
    }

    private void CaptureIdentityNotes()
    {
        foreach (var line in _identityNoteInputs) _identityNotes[line.Index] = _shell.Tree.GetComponent<XsrUiTextInput>(line.Input)!.ReadDraft();
    }

    private void BuildIdentityNoteRows()
    {
        foreach (var key in _identityActions.Keys.Where(entity => _shell.Tree.Name(entity).StartsWith("InstanceNote", StringComparison.Ordinal)).ToArray()) _identityActions.Remove(key);
        foreach (var child in _shell.Tree.Children(_identityNotesGroup).ToArray()) _shell.Tree.Destroy(child);
        _identityNoteInputs.Clear(); _identityNotesPage = Math.Clamp(_identityNotesPage, 0, (_identityNotes.Count - 1) / 32);
        int first = _identityNotesPage * 32;
        for (int index = first; index < Math.Min(_identityNotes.Count, first + 32); index++)
        {
            var input = Element(_identityNotesGroup, "InstanceNoteInput." + index, XsrUiSemanticRole.TextInput, "备注行 " + (index + 1), height: 30);
            _shell.Tree.SetComponent(input, new XsrUiTextInput { MaximumLength = 8192, PreserveTabs = true });
            _shell.Tree.SetComponent(input, new XsrUiInput { Focusable = true, Clickable = true });
            Style(input, new(244, 247, 251), Ink, 6); _shell.Renderer.SetTextInputValue(input, _identityNotes[index]);
            _identityNoteInputs.Add((input, index));
        }
        var actions = Stack(_identityNotesGroup, "InstanceNotesActions", XsrUiOrientation.Horizontal, 6);
        Add(actions, "InstanceNoteAdd", "添加备注行", () =>
        { if (_identityNotes.Count >= 8193) return; CaptureIdentityNotes(); _identityNotes.Add(""); _identityNotesPage = (_identityNotes.Count - 1) / 32; BuildIdentityNoteRows(); });
        if (_identityNotes.Count > 32)
        {
            Add(actions, "InstanceNotePrevious", "上一页", () => { CaptureIdentityNotes(); _identityNotesPage--; BuildIdentityNoteRows(); });
            Add(actions, "InstanceNoteNext", "下一页", () => { CaptureIdentityNotes(); _identityNotesPage++; BuildIdentityNoteRows(); });
            Text(actions, (_identityNotesPage + 1) + " / " + ((_identityNotes.Count - 1) / 32 + 1), 12, Muted, 24);
        }
        _shell.Tree.MarkDirty(_identityNotesGroup, XsrUiDirtyKinds.Layout);
    }

    private void StartIdentityRead()
    {
        if (_instance is not null && _queries.TryResolve(InstanceIdentityContract.Query, out var route))
        {
            _identityStop?.Cancel(); _identityStop?.Dispose(); _identityStop = new();
            _identityRead = _queries.QueryAsync<InstanceIdentityQuery, InstanceIdentitySnapshot>(route, new(_instance), cancellationToken: _identityStop.Token).AsTask();
            WakeOnPlatformCompletion(_identityRead);
        }
    }

    private void CancelIdentityRead()
    { _identityStop?.Cancel(); _identityStop?.Dispose(); _identityStop = null; _identityRead = null; _identityFailed = false; }

    private void UpdateIdentity()
    {
        if (_instance is null || _selected is not ("overview" or "servers")) return;
        if (_identityInstance != _instance)
        { CancelIdentityRead(); _identityInstance = _instance; _identity = null; }
        if (_identityWrite is { IsCompleted: true } written)
        {
            _identityWrite = null;
            if (written.IsCompletedSuccessfully && written.Result.IsSuccess)
            { _feedback.Info("实例信息已保存。"); _identity = null; _identityRead = null; StartIdentityRead(); RefreshManagement(); ManagementChanged?.Invoke(); }
            else _feedback.Error("实例信息未保存：" + (written.IsCompletedSuccessfully ? written.Result.Error?.Message : "请检查文件权限。"));
        }
        if (_identity is null && _identityRead is null && !_identityFailed) StartIdentityRead();
        if (_identityRead is not { IsCompleted: true } reading) return;
        _identityRead = null;
        if (PendingQuery.Succeeded(reading)) { _identity = reading.Result.Value; BuildIdentityControls(); }
        else { _identityFailed = true; _feedback.Error("无法读取实例信息；文件已保留。"); BuildIdentityControls(); }
    }

    private void SaveIdentity()
    {
        if (_identity is null || _identityWrite is not null || _instance is null || !_commands.TryResolve(InstanceIdentityContract.Save, out var route)) return;
        string Draft(string key, string fallback) => _identityInputs.TryGetValue(key, out var item)
            ? _shell.Tree.GetComponent<XsrUiTextInput>(item.Input)!.ReadDraft() is { } value && value != item.Presented ? value : item.Original : fallback;
        static string[] Parts(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var fields = _identity.Fields;
        var server = _identity.Server;
        if (_selected == "overview")
        {
            CaptureIdentityNotes();
            fields = fields with
            {
                DisplayName = Draft("name", fields.DisplayName),
                Description = Draft("description", fields.Description),
                IconPath = Draft("icon", fields.IconPath),
                Tags = Parts(Draft("tags", string.Join(',', fields.Tags))),
                Group = Draft("group", fields.Group),
                CustomInfo = Draft("custom-info", fields.CustomInfo),
                Starred = _identityStarred,
                InstanceIsolation = _identityIsolation,
                ModpackProject = Draft("modpack-project", fields.ModpackProject),
                ModpackVersion = Draft("modpack-version", fields.ModpackVersion),
                Notes = string.Join('\n', _identityNotes)
            };
        }
        else
        {
            if (!int.TryParse(Draft("login", server.LoginRequirement.ToString(System.Globalization.CultureInfo.InvariantCulture)), out int login)) { _feedback.Error("请选择有效的登录要求。"); return; }
            server = server with
            {
                LoginRequirement = login,
                AuthServerAddress = Draft("auth", server.AuthServerAddress),
                AuthRegisterAddress = Draft("register", server.AuthRegisterAddress),
                AuthServerDisplayName = Draft("auth-name", server.AuthServerDisplayName),
                DefaultServer = Draft("default-server", server.DefaultServer),
                ExpectedGameVersion = Draft("game-version", server.ExpectedGameVersion),
                ExpectedLoader = Draft("loader", server.ExpectedLoader),
                RequiredMods = Parts(Draft("mods", string.Join(',', server.RequiredMods))),
                OfflineLaunchAllowed = _identityOfflineAllowed
            };
        }
        _identityWrite = _commands.Dispatch(route, new InstanceIdentitySaveCommand(_instance, _identity.Revision, fields, server)).Completion;
        WakeOnPlatformCompletion(_identityWrite);
    }
}
