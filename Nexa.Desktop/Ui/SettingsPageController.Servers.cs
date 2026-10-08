using System.Globalization;
using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private InstanceServerList? _serverList;
    private Task<XsrResult<InstanceServerList>>? _serverRead;
    private CancellationTokenSource? _serverStop;
    private string? _serverInstance, _serverError;
    private bool _serverLoaded;
    private int? _serverEditing;
    private XsrUiEntityId _serverName, _serverAddress;
    internal Action<string, string>? JoinManagementServer { get; set; }
    private Task<XsrResult<InstanceServerStatus>>? _serverStatusRead;
    private int _serverStatusIndex;
    private readonly Dictionary<int, InstanceServerStatus> _serverStatuses = [];
    private readonly Dictionary<int, XsrUiEntityId> _serverStatusLabels = [];
    private void CancelServers()
    {
        _serverStop?.Cancel(); _serverStop?.Dispose(); _serverStop = null; _serverRead = null;
        _serverList = null; _serverInstance = null; _serverLoaded = false; _serverEditing = null; _serverError = null;
        _serverStatusRead = null; _serverStatuses.Clear(); _serverStatusLabels.Clear();
    }
    private void UpdateServers()
    {
        if (_selected != "servers") { if (_serverInstance is not null) CancelServers(); return; }
        if (_instance != _serverInstance) { CancelServers(); _serverInstance = _instance; }
        if (_selected != "servers" || _instance is null || !_managementLoaded) return;
        if (_serverStatusRead is { IsCompleted: true } status)
        {
            _serverStatusRead = null;
            if (PendingQuery.Succeeded(status)) _serverStatuses[_serverStatusIndex] = status.Result.Value!;
            if (_serverStatusLabels.TryGetValue(_serverStatusIndex, out var label) && _shell.Tree.IsAlive(label))
                _shell.Tree.SetComponent(label, new XsrUiText(ServerStatusLabel(_serverStatusIndex)) { Localize = false });
        }
        if (!_serverLoaded && _serverRead is null && _queries.TryResolve(InstanceServerListContract.Read, out var route))
        {
            _serverStop = new(); _serverRead = _queries.QueryAsync<InstanceServerListQuery, InstanceServerList>(route, new(_instance), cancellationToken: _serverStop.Token).AsTask();
            WakeOnPlatformCompletion(_serverRead);
        }
        if (_serverRead is not { IsCompleted: true } read) return;
        _serverRead = null; _serverLoaded = true;
        if (PendingQuery.Succeeded(read)) _serverList = read.Result.Value;
        else _serverError = "无法读取服务器列表。请检查 servers.dat，原文件未修改。";
        BuildSections();
    }
    private XsrUiEntityId ManagementField(XsrUiEntityId parent, string key, string caption, string value, string placeholder = "")
    {
        var row = Stack(parent, key + "Row", XsrUiOrientation.Horizontal, 12);
        var label = Text(row, caption, 13, Muted, 36); _shell.Tree.GetComponent<XsrUiElement>(label)!.Width = 100;
        var input = Element(row, key, XsrUiSemanticRole.TextInput, caption, height: 36);
        _shell.Tree.GetComponent<XsrUiElement>(input)!.Weight = 1; _shell.Tree.GetComponent<XsrUiElement>(input)!.Padding = new(12, 0, 12, 0);
        Style(input, new(241, 245, 250), Ink, 9, 13);
        _shell.Tree.SetComponent(input, new XsrUiTextInput { Placeholder = placeholder });
        _shell.Tree.SetComponent(input, new XsrUiInput { Focusable = true, Clickable = true });
        _shell.Renderer.SetTextInputValue(input, value); return input;
    }
    private void BuildServers()
    {
        _serverStatusLabels.Clear();
        if (_serverList is not { } list) { Text(_sections, _serverError ?? "正在读取服务器列表…", 13, Muted, 28); return; }
        var bar = Stack(_sections, "ServerToolbar", XsrUiOrientation.Horizontal, 10);
        Text(bar, $"{list.Entries.Count} 个服务器", 13, Muted, 36);
        ManagementButton(bar, "添加服务器", () => { _serverEditing = -1; BuildSections(); }, 108);
        if (_serverEditing is { } editing)
        {
            var entry = editing >= 0 ? list.Entries[editing] : new(-1, "", "");
            var editor = FormGroup(_sections, "ServerEditor", editing < 0 ? "添加服务器" : "编辑服务器");
            _serverName = ManagementField(editor, "ServerName", "名称", entry.Name);
            _serverAddress = ManagementField(editor, "ServerAddress", "地址", entry.Address, "example.org:25565");
            var actions = Stack(editor, "ServerEditorActions", XsrUiOrientation.Horizontal, 8);
            ManagementButton(actions, "取消", () => { _serverEditing = null; BuildSections(); }, 64);
            ManagementButton(actions, "保存", () =>
            {
                string name = _shell.Tree.GetComponent<XsrUiTextInput>(_serverName)!.ReadDraft(), address = _shell.Tree.GetComponent<XsrUiTextInput>(_serverAddress)!.ReadDraft();
                var updated = list.Entries.ToList(); var changed = entry with { Name = name, Address = address };
                if (editing < 0) updated.Add(changed); else updated[editing] = changed;
                SaveServers(updated);
            }, 64);
        }
        foreach (var (entry, index) in list.Entries.Select((e, i) => (e, i)))
        {
            var card = Stack(_sections, "ServerEntry." + index, XsrUiOrientation.Vertical, 8); Style(card, White, Ink, 12);
            var body = Stack(card, "ServerEntryBody", XsrUiOrientation.Vertical, 8);
            _shell.Tree.GetComponent<XsrUiElement>(body)!.Padding = new(16, 10, 16, 10);
            var row = Stack(body, "ServerEntryIdentity", XsrUiOrientation.Horizontal, 10);
            var identity = Stack(row, "ServerIdentity", XsrUiOrientation.Vertical, 2); _shell.Tree.GetComponent<XsrUiElement>(identity)!.Weight = 1;
            ContentName(identity, entry.Name, 15, 26); ContentName(identity, entry.Address, 12, 22, foreground: Muted);
            _serverStatusLabels[index] = Text(identity, ServerStatusLabel(index), 12, Muted, 24);
            DesktopLiteralText.Preserve(_shell.Tree, _serverStatusLabels[index]);
            ManagementButton(row, "检查状态", () =>
            {
                if (_serverStatusRead is not null || _serverInstance is null || !_queries.TryResolve(InstanceServerListContract.Status, out var route)) return;
                _serverStatusIndex = index;
                _serverStatusRead = _queries.QueryAsync<InstanceServerStatusQuery, InstanceServerStatus>(route, new(_serverInstance, list.Revision, entry.SourceIndex), cancellationToken: _serverStop?.Token ?? default).AsTask();
                _shell.Tree.SetComponent(_serverStatusLabels[index], new XsrUiText("正在连接…")); WakeOnPlatformCompletion(_serverStatusRead);
            }, 84);
            if (JoinManagementServer is not null) ManagementButton(row, "加入", () => JoinManagementServer(_serverInstance!, entry.Address), 60);
            var actions = Stack(body, "ServerEntryActions", XsrUiOrientation.Horizontal, 8);
            ManagementButton(actions, "编辑", () => { _serverEditing = index; BuildSections(); }, 60);
            if (index > 0) ManagementButton(actions, "上移", () =>
            { var items = list.Entries.ToList(); (items[index - 1], items[index]) = (items[index], items[index - 1]); SaveServers(items); }, 60);
            if (index + 1 < list.Entries.Count) ManagementButton(actions, "下移", () =>
            { var items = list.Entries.ToList(); (items[index + 1], items[index]) = (items[index], items[index + 1]); SaveServers(items); }, 60);
            ManagementButton(actions, "移除", () => _feedback.ShowDialog("server.remove", "移除服务器", "只从此版本的列表移除此服务器。", "移除", "取消", accepted => { if (accepted && _serverInstance == _instance && ReferenceEquals(_serverList, list)) SaveServers(list.Entries.Where((_, i) => i != index).ToArray()); }), 60);
        }
    }
    private string ServerStatusLabel(int index) => _serverStatuses.TryGetValue(index, out var status)
        ? status.Reachable ? $"{status.Milliseconds} ms · {status.OnlinePlayers?.ToString(CultureInfo.InvariantCulture) ?? "?"}/{status.MaxPlayers?.ToString(CultureInfo.InvariantCulture) ?? "?"} {_shell.Renderer.LocalizeText("人")} · {status.Version} · {ProtocolCompatibility(status)} · {status.Description}" : status.Description
        : _shell.Renderer.LocalizeText("状态未检查");
    private string ProtocolCompatibility(InstanceServerStatus status) => status.ServerProtocol is { } server && status.ClientProtocol is { } client
        ? server == client ? _shell.Renderer.LocalizeText("协议号一致（模组与认证另行检查）")
            : _shell.Renderer.LocalizeText("协议号不同") + ": " + _shell.Renderer.LocalizeText("客户端") + " " + client.ToString(CultureInfo.InvariantCulture)
                + " / " + _shell.Renderer.LocalizeText("服务器") + " " + server.ToString(CultureInfo.InvariantCulture)
        : _shell.Renderer.LocalizeText("协议兼容无法判定（缺少已校验客户端或服务器协议号）");
    private void SaveServers(IReadOnlyList<InstanceServerEntry> entries)
    {
        if (_managementWrite is not null || _instance is null || _serverList is null || !_commands.TryResolve(InstanceServerListContract.Save, out var route)) return;
        _managementWriteInstance = _instance;
        _managementWrite = _commands.Dispatch(route, new InstanceServerListSaveCommand(_instance, _serverList.Revision, entries)).Completion;
        WakeOnPlatformCompletion(_managementWrite);
    }
}
