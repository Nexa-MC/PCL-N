using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId ProxyMode = XsrSemanticId.Parse("ui.settings.proxy.mode");
    private static readonly XsrSemanticId ProxyApply = XsrSemanticId.Parse("ui.settings.proxy.apply");
    private static readonly XsrSemanticId ProxyReload = XsrSemanticId.Parse("ui.settings.proxy.reload");
    private static readonly string[] ProxyKeys = ["network.proxy-address", "network.proxy-user", "network.proxy-password"];
    private readonly Dictionary<string, XsrUiEntityId> _proxyInputs = [];
    private readonly Dictionary<XsrUiEntityId, string> _proxyModes = [];
    private readonly Dictionary<string, string> _proxyCommitted = [];
    private XsrUiEntityId _proxyTrack;
    private string _proxyDraftMode = "1";
    private string _proxyCommittedMode = "1";
    private long _proxyRevision = -1;
    private bool _proxyInitialized;
    private Task<XsrResult>? _proxyCommit;
    private Dictionary<string, string>? _proxySavedDrafts;

    private static bool IsProxyIntent(XsrSemanticId command) => command == ProxyMode || command == ProxyApply || command == ProxyReload;

    private void ResetProxyPreferences(bool preserveDraft = false)
    {
        _proxySavedDrafts = preserveDraft && _proxyInitialized && _selected == "network"
            ? _proxyInputs.Where(pair => _shell.Tree.IsAlive(pair.Value)).ToDictionary(pair => pair.Key,
                pair => _shell.Tree.GetComponent<XsrUiTextInput>(pair.Value)!.ReadDraft()) : null;
        _proxyInputs.Clear(); _proxyModes.Clear(); _proxyTrack = default;
        if (_proxySavedDrafts is null)
        { _proxyCommitted.Clear(); _proxyInitialized = false; _proxyRevision = -1; }
    }

    private void BuildProxyPreferences()
    {
        var group = FormGroup(_sections, "SettingsProxy", "代理");
        Text(group, "配置整组应用；凭据仅保存在本机，新请求生效。", 11, Muted, 24);
        var toolbar = Stack(group, "SettingsProxyToolbar", XsrUiOrientation.Horizontal, 8);
        _proxyTrack = Stack(toolbar, "SettingsProxyMode", XsrUiOrientation.Horizontal, 0);
        _shell.Tree.SetComponent(_proxyTrack, new XsrUiSemantic(XsrUiSemanticRole.RadioGroup, "代理模式草稿"));
        Style(_proxyTrack, new(241, 245, 250), Ink, 9);
        var thumb = Element(_proxyTrack, "SettingsProxyModeThumb", XsrUiSemanticRole.None, null);
        _shell.Tree.GetComponent<XsrUiElement>(thumb)!.IsVisible = false;
        Style(thumb, White, Ink, 7);
        _shell.Tree.SetComponent(thumb, new XsrUiTransition());
        _shell.Tree.SetComponent(_proxyTrack, new XsrUiSegmentedTrack(thumb));
        foreach (var (value, label) in new[] { ("0", "不使用"), ("1", "跟随系统"), ("2", "自定义") })
        {
            var option = RadioOption(_proxyTrack, "SettingsProxyMode." + value, label, ProxyMode, 84);
            _proxyModes[option] = value;
        }
        foreach (string key in ProxyKeys)
        {
            string label = key switch { "network.proxy-address" => "地址", "network.proxy-user" => "用户名", _ => "密码" };
            var row = Stack(group, "SettingsProxyRow." + key, XsrUiOrientation.Horizontal, 12);
            var caption = Text(row, label, 13, Ink, 34);
            _shell.Tree.GetComponent<XsrUiElement>(caption)!.Width = 84;
            var input = Element(row, "SettingsInput." + key, XsrUiSemanticRole.TextInput, label, height: 34);
            _shell.Tree.GetComponent<XsrUiElement>(input)!.Weight = 1;
            _shell.Tree.GetComponent<XsrUiElement>(input)!.Padding = new(10, 0, 10, 0);
            _shell.Tree.SetComponent(input, new XsrUiTextInput
            {
                IsPassword = key == "network.proxy-password",
                MaximumLength = key == "network.proxy-address" ? 2048 : 1024,
                Placeholder = key == "network.proxy-address" ? "http://localhost:8080" : label
            });
            _shell.Tree.SetComponent(input, new XsrUiInput { Focusable = true, Clickable = true });
            Style(input, new(245, 246, 248), Ink, 9);
            _proxyInputs[key] = input;
        }
        ActionButton(toolbar, "SettingsProxyApply", "应用代理", ProxyApply, 84);
        ActionButton(toolbar, "SettingsProxyReload", "重新读取", ProxyReload, 84);
        if (_proxySavedDrafts is not null)
        {
            foreach (var (key, input) in _proxyInputs)
                _shell.Renderer.SetTextInputValue(input, _proxySavedDrafts.GetValueOrDefault(key, ""));
            _proxySavedDrafts = null;
        }
        UpdateProxyPreferences();
    }

    private bool ProxyHasDraft => _proxyDraftMode != _proxyCommittedMode || _proxyInputs.Any(pair =>
        _shell.Tree.IsAlive(pair.Value) && _shell.Tree.GetComponent<XsrUiTextInput>(pair.Value)!.ReadDraft() != _proxyCommitted.GetValueOrDefault(pair.Key, ""));

    private void UpdateProxyPreferences()
    {
        if (_proxyCommit is { IsCompleted: true } completed)
        {
            _proxyCommit = null;
            if (PendingQuery.Succeeded(completed))
            {
                _proxyInitialized = false; _revision = -1;
                return;
            }
        }
        if (_values is null || !_proxyTrack.IsAssigned || !_shell.Tree.IsAlive(_proxyTrack)) return;
        if (!_proxyInitialized || _values.Revision != _proxyRevision && !ProxyHasDraft)
        {
            _proxyDraftMode = _proxyCommittedMode = _values.Values.Single(item => item.Key == "network.proxy-mode").Value.Value ?? "1";
            foreach (var (key, input) in _proxyInputs)
            {
                string value = _values.Values.Single(item => item.Key == key).Value.Value ?? "";
                _proxyCommitted[key] = value;
                _shell.Renderer.SetTextInputValue(input, value);
            }
            _proxyRevision = _values.Revision; _proxyInitialized = true;
        }
        foreach (var (option, value) in _proxyModes)
        {
            bool selected = value == _proxyDraftMode;
            _shell.Tree.GetComponent<XsrUiSelection>(option)!.IsSelected = selected;
            if (selected) _shell.Tree.GetComponent<XsrUiSegmentedTrack>(_proxyTrack)!.Selected = option;
            Style(option, XsrUiColor.Transparent, selected ? Blue : Ink, 7, 12, selected ? 600 : 450);
            _shell.Tree.GetComponent<XsrUiVisualStyle>(option)!.TextAlignment = XsrUiTextAlignment.Center;
        }
    }

    private void HandleProxyIntent(DesktopUiIntent intent)
    {
        if (_selected != "network" || _instanceDirectory is not null || _writing is not null || !_proxyInitialized
            || !_shell.Tree.IsAlive(intent.Source)) return;
        if (intent.Command == ProxyMode && _proxyModes.TryGetValue(intent.Source, out var mode))
        { _proxyDraftMode = mode; UpdateProxyPreferences(); return; }
        if (intent.Command == ProxyReload && _shell.Tree.Name(intent.Source) == "SettingsProxyReload")
        { _proxyInitialized = false; UpdateProxyPreferences(); return; }
        if (intent.Command != ProxyApply || _shell.Tree.Name(intent.Source) != "SettingsProxyApply"
            || !_commands.TryResolve(SettingsPolicyContract.BatchCommand, out var route)) return;
        List<SettingsMutation> changes = [new("network.proxy-mode", SettingsLayer.Global, new(SettingsOverrideMode.Custom, _proxyDraftMode))];
        foreach (var (key, input) in _proxyInputs)
            changes.Add(new(key, SettingsLayer.Global, new(SettingsOverrideMode.Custom, _shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft())));
        _proxyCommit = _writing = SaveProxyAsync(route, new(changes.AsReadOnly(), _proxyRevision));
    }

    private async Task<XsrResult> SaveProxyAsync(XsrCommandId route, SettingsBatchCommand command)
    {
        var result = await _commands.Dispatch(route, command).Completion.ConfigureAwait(false);
        if (!_disposed && !result.IsSuccess) _feedback.Error("代理配置未保存：" + result.Error?.Message);
        return result;
    }
}
