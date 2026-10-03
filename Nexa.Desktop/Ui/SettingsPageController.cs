using System.Collections.Concurrent;
using Nexa.Pxml;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>Settings presentation only. Value validation, availability and writes use sealed Service routes.</summary>
internal sealed partial class SettingsPageController : IDisposable
{
    private static readonly XsrSemanticId Select = XsrSemanticId.Parse("ui.settings.section");
    private static readonly XsrSemanticId Edit = XsrSemanticId.Parse("ui.settings.edit");
    private static readonly XsrSemanticId Choice = XsrSemanticId.Parse("ui.settings.choice");
    private static readonly XsrSemanticId Inherit = XsrSemanticId.Parse("ui.settings.inherit");
    private readonly Func<string?>? _instanceDirectory;
    private string? _instance;
    private readonly Dictionary<XsrUiEntityId, string> _inheritButtons = [];
    private IReadOnlyList<SettingsCatalogPage> Pages => _instanceDirectory is null ? _catalog!.GlobalPages : ManagementPages;
    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly XsrQueryRouter _queries;
    private readonly XsrCommandRouter _commands;
    private readonly XsrStateStore _store;
    private readonly DesktopFeedbackService _feedback;
    private readonly ConcurrentQueue<DesktopUiIntent> _pending = new();
    private readonly Dictionary<XsrUiEntityId, string> _navigation = [];
    private readonly Dictionary<XsrUiEntityId, Editor> _editors = [];
    private readonly Dictionary<XsrUiEntityId, (Editor Editor, string Value)> _choices = [];
    private readonly Dictionary<string, double> _scrollPositions = [];
    private readonly XsrUiEntityId _navigationRoot, _pager;
    private XsrUiEntityId _sections;
    private readonly Dictionary<string, XsrUiEntityId> _pages = [];
    private Task<XsrResult<SettingsCatalogSnapshot>>? _catalogReading;
    private readonly XsrStateId _revisionId;
    private SettingsCatalogSnapshot? _catalog;
    private SettingsEffectiveSnapshot? _values;
    private Task<XsrResult<SettingsEffectiveSnapshot>>? _reading;
    private Task<XsrResult>? _writing;
    private string _selected = "general";
    private bool _developer, _disposed;
    private bool _visible;
    private XsrUiThickness _previousContentPadding;
    private long _revision = -1;
    private sealed record Editor(SettingsCatalogEntry Entry, XsrUiEntityId Input, XsrUiEntityId Button, XsrUiEntityId Thumb);
    private static readonly XsrUiColor Ink = new(43, 51, 64), Muted = new(113, 124, 140), Blue = new(11, 91, 203), White = new(255, 255, 255), Line = new(232, 236, 242);

    public SettingsPageController(XsrUiShell shell, DesktopUiIntentSink intents, XsrQueryRouter queries, XsrCommandRouter commands,
        XsrStateStore store, DesktopFeedbackService feedback, Func<string?>? instanceDirectory = null)
    {
        _shell = shell; _intents = intents; _queries = queries; _commands = commands; _store = store; _feedback = feedback;
        _instanceDirectory = instanceDirectory;
        if (instanceDirectory is not null) _selected = "overview";
        _revisionId = store.Resolve(SettingsPolicyContract.RevisionKey);
        using var stream = typeof(SettingsPageController).Assembly.GetManifestResourceStream("Nexa.Desktop.Ui.SettingsPage.pxml")!;
        using var reader = new StreamReader(stream);
        var host = shell.Tree.Create("settings-loader");
        string markup = reader.ReadToEnd();
        if (instanceDirectory is not null) markup = markup.Replace("Key=\"SettingsPage\" Label=\"设置\"", "Key=\"VersionSettingsPage\" Label=\"版本设置\"", StringComparison.Ordinal);
        Page = PxmlUiLoader.Load(PxmlCompiler.Compile(PxmlParser.Parse(markup)), shell.Tree, store, host);
        shell.Tree.Detach(Page); shell.Tree.Destroy(host);
        var names = new Dictionary<string, XsrUiEntityId>();
        shell.Tree.Walk(Page, entity => { names[shell.Tree.Name(entity)] = entity; return true; });
        _navigationRoot = names["SettingsNavigation"]; _pager = names["SettingsPager"];
        shell.Tree.SetComponent(_navigationRoot, new XsrUiScrollGesture());
        shell.Tree.SetComponent(_navigationRoot, new XsrUiSemantic(XsrUiSemanticRole.RadioGroup, instanceDirectory is null ? "设置分类" : "版本设置分类"));
        Style(_navigationRoot, new(241, 245, 250), Ink, 10);
        shell.Tree.GetComponent<XsrUiElement>(_navigationRoot)!.HorizontalAlignment = XsrUiAlignment.Start;
        shell.Tree.SetComponent(_navigationRoot, new XsrUiSegmentedTrack(names["SettingsThumb"]));
        shell.Tree.SetComponent(names["SettingsThumb"], new XsrUiTransition());
        Style(names["SettingsThumb"], White, Ink, 8);
        _intents.IntentEmitted += OnIntent;
        _shell.Renderer.FramePreparing += OnFrame;
    }

    internal XsrUiEntityId Page { get; }
    internal string SelectedSection => _selected;
    internal bool TelemetryRequired { get; set; }

    private void OnIntent(object? sender, DesktopUiIntentEventArgs args)
    {
        if (_shell.Stage.Navigation.Current != Page) return;
        if (IsJavaInventoryIntent(args.Intent.Command) || IsSettingsTransferIntent(args.Intent.Command) || IsUpdateIntent(args.Intent.Command) || args.Intent.Command == ManagementAction || args.Intent.Command == Inherit || args.Intent.Command == Select || args.Intent.Command == Edit || args.Intent.Command == Choice || args.Intent.Command == ArgumentAdd || args.Intent.Command == ArgumentRemove || args.Intent.Command == RefreshPlatform) _pending.Enqueue(args.Intent);
        else if (args.Intent.Command == RemediationExecuted) OnPlatformRemediation(sender, args);
    }
    private void OnFrame(object? sender, EventArgs args)
    {
        if (_disposed) return;
        bool visible = _shell.Stage.Navigation.Current.Equals(Page);
        if (visible != _visible)
        {
            var content = _shell.Tree.GetComponent<XsrUiElement>(_shell.Content)!;
            if (visible) { _previousContentPadding = content.Padding; content.Padding = default; }
            else
            {
                content.Padding = _previousContentPadding; CancelManagementRead(); CancelOnlineContent(); CancelModRemovalPreview(); CancelSettingsTransfer(); CancelJavaInventory();
                ReleaseContentGraph();
            }
            _visible = visible;
            _shell.Tree.MarkDirty(_shell.Content, XsrUiDirtyKinds.Layout);
        }
        UpdateReleaseCheck();
        if (!visible) return;
        string? instance = _instanceDirectory?.Invoke();
        if (_instanceDirectory is not null && string.IsNullOrWhiteSpace(instance))
        {
            bool transferWasBusy = SettingsTransferBusy;
            CancelSettingsTransfer();
            if (transferWasBusy && _catalog is not null) BuildSections();
            return;
        }
        if (instance != _instance)
        {
            CancelSettingsTransfer(); CancelJavaInventory();
            _instance = instance; _reading = null; _values = null; _revision = -1;
            ResetManagement();
            if (_catalog is not null) BuildSections(navigating: true);
        }
        if (_instanceDirectory is null) UpdatePlatformCapabilities();
        if (_catalog is null)
        {
            if (!_queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var route)) return;
            _catalogReading ??= _queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(route, new(true)).AsTask();
            if (!_catalogReading.IsCompleted) return;
            if (!new PendingQuery<SettingsCatalogSnapshot>(_catalogReading).TryRead(out var result)) { _catalogReading = null; return; }
            _catalogReading = null;
            if (!result.IsSuccess) return;
            _catalog = result.Value!;
            BuildNavigation(); BuildSections();
        }
        if (_writing is { IsCompleted: true }) _writing = null;
        long revision = _store.Read<long>(_revisionId).Value;
        if (_reading is null && _revision != revision && _queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var read))
            _reading = _queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(read, new(_instance)).AsTask();
        if (_reading is { IsCompleted: true } reading)
        {
            _reading = null;
            if (PendingQuery.Succeeded(reading))
            {
                if (_instanceDirectory is not null && _selected == "recovery" && _revision >= 0 && _revision != reading.Result.Value!.Revision)
                    CancelManagementRead();
                _values = reading.Result.Value!; _revision = _values.Revision;
                bool developer = _values.Values.First(item => item.Key == "developer.enabled").Value.Value == "true";
                if (developer != _developer) { _developer = developer; BuildSections(); }
                UpdateEditors();
            }
            else { _revision = revision; _feedback.Error("无法读取设置，请重新打开设置页。"); }
        }
        while (_pending.TryDequeue(out var intent))
        {
            if (IsJavaInventoryIntent(intent.Command)) { HandleJavaInventory(intent.Command, intent.Source); continue; }
            if (IsSettingsTransferIntent(intent.Command)) { HandleSettingsTransfer(intent.Command, intent.Source); continue; }
            if (intent.Command == ManagementAction && _managementActions.TryGetValue(intent.Source, out var action)) { action(); continue; }
            if (IsUpdateIntent(intent.Command)) { HandleUpdateIntent(intent.Command); continue; }
            if (intent.Command == Inherit && _inheritButtons.TryGetValue(intent.Source, out var inheritKey)
                && _writing is null && _commands.TryResolve(SettingsPolicyContract.SetCommand, out var inheritRoute))
            {
                _writing = SaveAsync(inheritRoute, new(inheritKey, SettingsLayer.Instance, new(SettingsOverrideMode.Inherit), _instance));
                continue;
            }
            if (intent.Command == RefreshPlatform) { RefreshPlatformCapabilities(); continue; }
            if (intent.Command == Select && _navigation.TryGetValue(intent.Source, out string? page) && page != _selected)
            {
                SwitchPage(page);
                _shell.Renderer.SelectPagerPage(_pager, Pages.ToList().FindIndex(item => item.Id == page));
            }
            else if (intent.Command == Edit && _editors.TryGetValue(intent.Source, out var editor) && _writing is null)
            {
                Save(editor);
            }
            else if (intent.Command == ArgumentAdd || intent.Command == ArgumentRemove) HandleArgumentIntent(intent);
            else if (intent.Command == Choice && _choices.TryGetValue(intent.Source, out var choice))
            {
                if (_writing is null)
                {
                    Save(choice.Editor, choice.Value);
                }
            }
        }
        UpdateManagement();
        UpdateOnlineContent();
        UpdateModRemovalPreview();
        UpdateContentGraph(); UpdateServers(); UpdateExport();
        UpdateSettingsTransfer(); UpdateJavaInventory();
        int index = _shell.Tree.GetComponent<XsrUiPager>(_pager)!.PageIndex;
        if (index >= 0 && index < Pages.Count && Pages[index].Id != _selected)
            SwitchPage(Pages[index].Id);
        UpdateContentWindow();
        var pager = _shell.Tree.GetComponent<XsrUiPager>(_pager)!;
        if (!pager.IsDragging && Math.Abs(pager.Position - pager.PageIndex) < 0.001)
            foreach (var page in _pages.Values)
                if (page != _sections)
                    foreach (var child in _shell.Tree.Children(page).ToArray()) _shell.Tree.Destroy(child);
    }

    private void SwitchPage(string page)
    {
        CancelSettingsTransfer(); CancelJavaInventory();
        _scrollPositions[_selected] = _shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY;
        if (_selected == "contentgraph")
        {
            if (_managementRead is not null) CancelManagementRead();
            ReleaseContentGraph();
        }

        _contentDetail = null; _contentFilter = "";
        _selected = page; _sections = _pages[page];
        if (_instanceDirectory is not null && page == "recovery")
        {
            CancelManagementRead();
            if (_management is { } management) _management = management with { RecoveryStorage = null, RecoveryComparison = null };
        }
        if (_instanceDirectory is not null && page == "trash") CancelManagementRead();
        if (_instanceDirectory is not null && page == "contentgraph")
        {
            ResetContentGraph();
            CancelManagementRead();
            if (_management is { } management) _management = management with { ContentGraph = null };
        }
        BuildSections(navigating: true); UpdateNavigation(); UpdateEditors();
    }

    private void BuildNavigation()
    {
        foreach (var page in Pages)
        {
            if (!_pages.TryGetValue(page.Id, out var body))
            {
                body = Stack(_pager, "SettingsSections", XsrUiOrientation.Vertical, 28);
                var layout = _shell.Tree.GetComponent<XsrUiElement>(body)!;
                layout.VerticalAlignment = XsrUiAlignment.Stretch; layout.Padding = new(20, 16, 20, 16);
                _shell.Tree.SetComponent(body, new XsrUiScroll { ShowsVerticalIndicator = true });
                _shell.Tree.SetComponent(body, new XsrUiScrollGesture());
                _shell.Tree.SetComponent(body, new XsrUiTransition { Key = page.Id, MovesSelf = true });
                _pages[page.Id] = body;
            }
            _shell.Tree.Detach(body); _shell.Tree.Attach(body, _pager);
            if (page.Id == _selected) _sections = body;
            var button = Element(_navigationRoot, "SettingsNav." + page.Id, XsrUiSemanticRole.RadioButton, page.Label, width: Math.Max(64, page.Label.Length * 14 + 24), height: 36);
            _shell.Tree.SetComponent(button, new XsrUiText(page.Label));
            _shell.Tree.SetComponent(button, new XsrUiInput { Focusable = true, Clickable = true });
            _shell.Tree.SetComponent(button, new XsrUiCommandBinding(Select));
            _shell.Tree.SetComponent(button, new XsrUiSelection());
            _navigation[button] = page.Id;
        }
        UpdateNavigation();
    }

    private void UpdateNavigation()
    {
        foreach (var pair in _navigation)
        {
            bool selected = pair.Value == _selected;
            Style(pair.Key, XsrUiColor.Transparent, selected ? Blue : Ink, 8, 13, selected ? 600 : 450);
            _shell.Tree.GetComponent<XsrUiVisualStyle>(pair.Key)!.TextAlignment = XsrUiTextAlignment.Center;
            if (selected) _shell.Tree.GetComponent<XsrUiSegmentedTrack>(_navigationRoot)!.Selected = pair.Key;
            _shell.Tree.GetComponent<XsrUiSelection>(pair.Key)!.IsSelected = selected;
        }
    }

    private void UpdateNavigationWidths()
    {
        foreach (var button in _navigation.Keys)
        {
            string label = _shell.Renderer.LocalizeText(_shell.Tree.GetComponent<XsrUiText>(button)!.Content);
            double width = Math.Max(64, label.Sum(character => character > 127 ? 14 : 8) + 24);
            var element = _shell.Tree.GetComponent<XsrUiElement>(button)!;
            if (element.Width == width) continue;
            element.Width = width;
            _shell.Tree.MarkDirty(button, XsrUiDirtyKinds.Layout);
        }
    }

    private void BuildSections(bool navigating = false)
    {

        string? focus = !navigating && _shell.Tree.IsAlive(_shell.Renderer.Focused) ? _shell.Tree.Name(_shell.Renderer.Focused) : null;
        var drafts = !navigating ? _editors.Values.Where(item => item.Input.IsAssigned).ToDictionary(item => item.Entry.Id,
            item => _shell.Tree.GetComponent<XsrUiTextInput>(item.Input)!.ReadDraft()) : [];
        if (!navigating) _scrollPositions[_selected] = _shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY;
        foreach (var child in _shell.Tree.Children(_sections).ToArray()) _shell.Tree.Destroy(child);
        _javaChoices.Clear();
        _javaInventoryGroup = default;
        _editors.Clear(); _inheritButtons.Clear(); _selectors.Clear(); _autoModes.Clear(); _argumentEditors.Clear(); _argumentActions.Clear(); _choices.Clear();
        _managementActions.Clear(); _contentSearch = default; _contentList = default; _contentWindowStart = -1;
        _graphSearch = _graphBody = default; _graphActions.Clear();
        if (_instanceDirectory is not null && _selected != "game")
        {
            BuildManagementSection();
            if (focus == "ManagementContentSearch" && _contentSearch.IsAssigned) _shell.Renderer.Focus(_contentSearch, showIndicator: false);
            return;
        }
        if (_selected == "platform") { BuildPlatformCapabilities(); return; }
        if (_selected == "about") { BuildAboutPage(); return; }
        if (_selected == "advanced") BuildUpdateCard();
        bool transfer = _instanceDirectory is null && _selected == "storage" || _instanceDirectory is not null && _selected == "game";
        if (_selected == "privacy")
        {
            var notice = Text(_sections, "必要遥测始终启用，仅包含版本、系统、架构及分类运行结果。诊断信息包括脱敏错误堆栈、耗时、资源占用、算法指标、功能使用情况、模组清单、加载器版本和游戏设置变化；正式版可关闭，测试版必须启用。不上传账户、路径或日志正文。", 12, Muted, height: 72);
            _shell.Tree.GetComponent<XsrUiVisualStyle>(notice)!.WrapText = true;
        }
        var entries = _catalog!.Entries.Where(item => item.Scope == "global"
            && (item.Page == _selected || _instanceDirectory is not null && _selected == "game" && item.Page == "java")
            && (_instanceDirectory is null || item.Definition?.InstanceOverride == true) && !item.IsRuntimeDetail && (_developer || !item.DeveloperOnly)).ToArray();
        var available = entries.Where(item => item.Kind == SettingsCatalogEntryKind.Setting
            && item.Availability == SettingsCapabilityAvailability.Available && item.Definition is not null).ToArray();
        if (available.Length == 0 && _selected != "advanced" && !transfer)
        {
            Text(_sections, Pages.First(page => page.Id == _selected).Label, 20, Ink, height: 30, weight: 600);
            Text(_sections, "此分类的设置正在准备中。", 13, Muted, height: 24);
        }
        _shell.Tree.GetComponent<XsrUiStackPanel>(_sections)!.Spacing = 18;
        foreach (var section in available.GroupBy(item => (Section: item.DeveloperOnly ? "开发者" : _instanceDirectory is not null ? InstanceSettingGroup(item) : item.Section, item.DeveloperOnly))
            .OrderBy(group => _instanceDirectory is null ? 0 : group.Key.Section switch { "启动与窗口" => 0, "Java 与内存" => 1, "服务器" => 2, _ => 3 }))
        {
            var cardContent = FormGroup(_sections, "SettingsGroup." + section.First().Id, DisplayLabel(section.Key.Section));
            var rows = section.ToArray();
            for (int i = 0; i < rows.Length; i++)
            {
                BuildRow(cardContent, rows[i]);
                if (i + 1 < rows.Length)
                    Style(Element(cardContent, "SettingsRowDivider", XsrUiSemanticRole.None, null, height: 1), Line, Muted, 0);
            }
            if (_instanceDirectory is not null && section.Key.Section == "Java 与内存") BuildJavaInventory(cardContent);
        }
        if (transfer) BuildSettingsTransfer();
        BuildJavaInventory();
        var scroll = _shell.Tree.GetComponent<XsrUiScroll>(_sections)!;
        scroll.OffsetY = _scrollPositions.GetValueOrDefault(_selected);
        _shell.Tree.GetComponent<XsrUiTransition>(_sections)!.Key = _selected + ":" + _developer;
        _shell.Tree.MarkDirty(_sections, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        foreach (var editor in _editors.Values)
        {
            if (editor.Input.IsAssigned && drafts.TryGetValue(editor.Entry.Id, out var draft)) _shell.Renderer.SetTextInputValue(editor.Input, draft);
            var target = editor.Input.IsAssigned && _shell.Tree.Name(editor.Input) == focus ? editor.Input : _shell.Tree.Name(editor.Button) == focus ? editor.Button : default;
            if (target.IsAssigned) _shell.Renderer.Focus(target);
        }
        if (focus is not null)
            _shell.Tree.Walk(_sections, entity => { if (_shell.Tree.Name(entity) == focus) _shell.Renderer.Focus(entity); return true; });
    }

    private XsrUiEntityId SettingsCard(string name, XsrUiThickness padding, double spacing = 0, double radius = 12)
    {
        var surface = Stack(_sections, name, XsrUiOrientation.Vertical, 0);
        Style(surface, White, Ink, radius);
        var body = Stack(surface, name + ".Body", XsrUiOrientation.Vertical, spacing);
        _shell.Tree.GetComponent<XsrUiElement>(body)!.Padding = padding;
        return body;
    }

    private void BuildRow(XsrUiEntityId parent, SettingsCatalogEntry entry)
    {
        var row = Stack(parent, "SettingsRow." + entry.Id, XsrUiOrientation.Horizontal, 16);
        _shell.Tree.GetComponent<XsrUiElement>(row)!.MinHeight = 48;
        _shell.Tree.GetComponent<XsrUiElement>(row)!.Padding = new(0, 6, 0, 6);
        var label = Stack(row, "SettingsLabel." + entry.Id, XsrUiOrientation.Vertical, 3);
        _shell.Tree.GetComponent<XsrUiElement>(label)!.Width = entry.SettingKey == "diagnostics.telemetry" ? 240 : 176;
        Text(label, entry.SettingKey == "diagnostics.telemetry" ? "诊断信息 · 用户体验改进计划" : DisplayLabel(entry.Label), 14, Ink, height: 22, weight: 500);
        if (SettingHint(entry.SettingKey) is { } hint)
        {
            var description = Text(label, hint, 11, Muted, height: 32);
            _shell.Tree.GetComponent<XsrUiText>(description)!.MaxLines = 2;
            _shell.Tree.GetComponent<XsrUiVisualStyle>(description)!.WrapText = true;
            _shell.Tree.GetComponent<XsrUiSemantic>(description)!.Label = hint;
        }
        bool enabled = entry.Availability == SettingsCapabilityAvailability.Available && entry.Definition is not null;
        if (entry.SettingKey == "diagnostics.telemetry" && TelemetryRequired)
        {
            Text(row, "测试版本必须启用", 12, Muted, height: 34);
            return;
        }
        if (!enabled)
        {
            var unavailable = Text(row, "尚未可用", 11, Muted, height: 28);
            var layout = _shell.Tree.GetComponent<XsrUiElement>(unavailable)!;
            layout.Width = 76; layout.VerticalAlignment = XsrUiAlignment.Center;
            Style(unavailable, new(245, 246, 248), Muted, 8, 11);
            _shell.Tree.GetComponent<XsrUiVisualStyle>(unavailable)!.TextAlignment = XsrUiTextAlignment.Center;
            _shell.Tree.GetComponent<XsrUiSemantic>(unavailable)!.Label = entry.Label + "，尚未可用";
            return;
        }
        if (_instanceDirectory is not null)
        {
            var inherit = ActionButton(row, "SettingsInherit." + entry.SettingKey, "继承", Inherit, 64);
            _inheritButtons[inherit] = entry.SettingKey!;
        }
        var definition = entry.Definition!;
        if (entry.SettingKey is "game.jvm" or "game.arguments")
        {
            _shell.Tree.GetComponent<XsrUiElement>(label)!.VerticalAlignment = XsrUiAlignment.Start;
            BuildArgumentEditor(row, entry); return;
        }
        if (definition.Kind is SettingsValueKind.Enum or SettingsValueKind.Boolean || entry.SettingKey == "general.region")
        {
            var slot = Element(row, "SettingsControlSlot", XsrUiSemanticRole.None, null, height: 40);
            _shell.Tree.GetComponent<XsrUiElement>(slot)!.Weight = 1;
            BuildShiftSelector(slot, entry);
            return;
        }
        XsrUiEntityId input = default;
        if (definition.Kind is SettingsValueKind.Number or SettingsValueKind.Text or SettingsValueKind.Path)
        {
            input = Element(row, "SettingsInput." + entry.SettingKey, XsrUiSemanticRole.TextInput, entry.Label, height: 34);
            _shell.Tree.GetComponent<XsrUiElement>(input)!.Weight = 1;
            _shell.Tree.SetComponent(input, new XsrUiTextInput
            {
                Placeholder = entry.SettingKey switch
                { "java.runtime" => "自动选择，或输入 Java 可执行文件路径", "game.memory" => "自动分配，或输入 MiB", _ => entry.Label }
            });
            _shell.Tree.SetComponent(input, new XsrUiInput { Focusable = true, Clickable = true });
            Style(input, new(245, 246, 248), Ink, 9, 13);
            _shell.Tree.GetComponent<XsrUiElement>(input)!.Padding = new(10, 0, 10, 0);
        }
        var button = ActionButton(row, "SettingsEdit." + entry.SettingKey, "应用", Edit, 44);
        _editors[button] = new(entry, input, button, default);
        if (definition.SupportsAuto)
        {
            BuildAutomaticMode(row, _editors[button]);
        }
    }

    private static string? SettingHint(string? key) => key switch
    {
        "general.region" => "用于日期和数字；更改后重启生效。",
        "updates.channel" => "用于下次检查；CI 使用 Alpha 更新。已开始的更新保持原通道。",
        "updates.auto-check" => "启动时检查一次；关闭后仍可手动检查。安装更新前需要确认。",
        "diagnostics.log-level" => "立即生效；自动沿用当前构建的日志等级。",
        "diagnostics.log-lines" => "界面保留的日志条数，50–2000；不会删除磁盘日志。",
        "game.process-priority" => "下次启动生效；系统可能拒绝提高优先级。",
        "game.title" => "下次启动生效；留空保留游戏标题。",
        "game.launcher-visibility" => "启动完成后生效；游戏异常退出时恢复启动器。",
        "game.jvm" => "每行一个参数，应用后用于下次启动。",
        "install.inherit-vanilla" => "关闭时安装独立版本；开启后依赖原版。下次安装生效。",
        "game.arguments" => "传递给 Minecraft 的额外启动参数。",
        "game.memory" => "使用 MiB；自动模式按现有内存策略分配。",
        "java.auto-install" => "缺少兼容 Java 时自动下载；关闭时先询问。",
        "java.vendor" => "优先选择兼容的发行版；未找到时使用其他兼容 Java。",
        "network.file-concurrency" => "同时处理的游戏文件数，1–64；新安装和启动前补全生效。",
        "network.file-retry" => "失败后额外重试一次；不影响来源切换与文件校验。",
        "network.game-source" => "游戏文件与运行库的下载顺序；海外使用原始来源。",
        "game.server" => "默认地址，可带端口；版本单独设置与临时加入优先。",
        "game.auto-repair" => "启动前补全缺失或损坏的游戏文件；不关闭预检。",
        "appearance.animations-disabled" => "减少界面切换和展开时的动态效果。",
        "appearance.animation-fps" => "动画时钟的目标帧率，1–240 fps；不改变动画时长。",
        "appearance.low-power" => "窗口在后台且没有任务时降低动画帧率，返回后自动恢复。",
        _ => null,
    };

    private void UpdateEditors()
    {
        foreach (var editor in _editors.Values)
        {
            var value = _values?.Values.FirstOrDefault(item => item.Key == editor.Entry.SettingKey);
            if (value is null) continue;
            string raw = value.Value.Value ?? "";
            if (editor.Input.IsAssigned && value.Value.Mode != SettingsOverrideMode.Auto && !_shell.Renderer.Focused.Equals(editor.Input))
                _shell.Renderer.SetTextInputValue(editor.Input, raw);
        }
        foreach (var (button, key) in _inheritButtons)
        {
            var value = _values?.Values.FirstOrDefault(item => item.Key == key);
            var text = _shell.Tree.GetComponent<XsrUiText>(button)!;
            string label = value?.Source == SettingsLayer.Instance ? "恢复继承" : "继承中";
            if (text.Content != label) { text.Content = label; _shell.Tree.MarkDirty(button, XsrUiDirtyKinds.Paint); }
        }
        UpdateNavigationWidths(); UpdateShiftSelectors(); UpdateAutomaticModes(); UpdateArgumentEditors();
    }

    private void Save(Editor editor, string? selectedValue = null)
    {
        if (!_commands.TryResolve(SettingsPolicyContract.SetCommand, out var route) || _values is null) return;
        if (_argumentEditors.TryGetValue(editor.Button, out var arguments))
        {
            _writing = SaveAsync(route, new(editor.Entry.SettingKey!, (_instanceDirectory is null ? SettingsLayer.Global : SettingsLayer.Instance),
                new(SettingsOverrideMode.Custom, string.Join("\n", ReadArgumentDrafts(arguments).Where(value => !string.IsNullOrWhiteSpace(value)))), _instance));
            return;
        }
        var current = _values.Values.First(item => item.Key == editor.Entry.SettingKey).Value.Value;
        if (selectedValue == "__manual__")
        {
            selectedValue = _shell.Tree.GetComponent<XsrUiTextInput>(editor.Input)!.ReadDraft();
            if (string.IsNullOrWhiteSpace(selectedValue))
            {
                if (editor.Entry.Definition!.Kind == SettingsValueKind.Number) selectedValue = editor.Entry.Definition.DefaultValue;
                else { _feedback.Info("请先填写 Java 可执行文件路径，再选择手动。"); return; }
            }
        }
        string raw = selectedValue ?? (editor.Input.IsAssigned ? _shell.Tree.GetComponent<XsrUiTextInput>(editor.Input)!.ReadDraft()
            : editor.Entry.Definition!.Kind == SettingsValueKind.Boolean ? (current == "true" ? "false" : "true") : (current == "fullscreen" ? "windowed" : "fullscreen"));
        var value = editor.Entry.Definition!.SupportsAuto && selectedValue == "auto"
            || editor.Entry.SettingKey == "java.runtime" && string.IsNullOrWhiteSpace(raw)
            ? new SettingsOverride(SettingsOverrideMode.Auto) : new(SettingsOverrideMode.Custom, raw);
        _writing = SaveAsync(route, new(editor.Entry.SettingKey!, (_instanceDirectory is null ? SettingsLayer.Global : SettingsLayer.Instance), value, _instance));
    }
    private async Task<XsrResult> SaveAsync(XsrCommandId route, SettingsMutation mutation)
    {
        var result = await _commands.Dispatch(route, mutation).Completion.ConfigureAwait(false);
        if (!_disposed && !result.IsSuccess) _feedback.Error("设置未保存：" + result.Error?.Message);
        return result;
    }

    private XsrUiEntityId Element(XsrUiEntityId parent, string name, XsrUiSemanticRole role, string? label, double? width = null, double? height = null)
    {
        var entity = _shell.Tree.Create(name); _shell.Tree.Attach(entity, parent);
        _shell.Tree.SetComponent(entity, new XsrUiElement { Width = width, Height = height, VerticalAlignment = XsrUiAlignment.Center });
        _shell.Tree.SetComponent(entity, new XsrUiSemantic(role, label));
        return entity;
    }
    private XsrUiEntityId Stack(XsrUiEntityId parent, string name, XsrUiOrientation orientation, double spacing)
    {
        var entity = Element(parent, name, XsrUiSemanticRole.None, null);
        _shell.Tree.SetComponent(entity, new XsrUiStackPanel(orientation) { Spacing = spacing });
        return entity;
    }
    private XsrUiEntityId Text(XsrUiEntityId parent, string content, double size, XsrUiColor ink, double height, double weight = 400)
    {
        var entity = Element(parent, "SettingsText", XsrUiSemanticRole.Text, content, height: height);
        _shell.Tree.SetComponent(entity, new XsrUiText(content) { MaxLines = 1, TrimOverflow = true }); Style(entity, XsrUiColor.Transparent, ink, 0, size, weight);
        return entity;
    }
    private void Style(XsrUiEntityId entity, XsrUiColor background, XsrUiColor foreground, double radius, double size = 13, double weight = 400)
    {
        _shell.Tree.SetComponent(entity, new XsrUiVisualStyle { Background = background, Foreground = foreground, CornerRadius = radius, FontSize = size, FontWeight = weight, Surface = XsrUiSurfaceKind.Solid, Hover = DesktopUiPalette.CapsuleHover });
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }
    private static string DisplayLabel(string label) => label switch
    {
        "[ Off / On ]" => "开发者选项",
        "Launcher Logo" => "启动器标志",
        "Launcher 透明度" => "启动器透明度",
        "GPU / Renderer" => "显卡与渲染器",
        "Game Resource Handoff" => "游戏资源交接",
        "Assets 验证策略" => "资源文件验证策略",
        "Launch Preflight" => "启动前检查",
        "Compatibility Check" => "兼容性检查",
        "自动 Repair Plan" => "自动生成修复方案",
        "JVM Arguments" => "JVM 参数",
        "Game Arguments" => "游戏参数",
        "Wrapper Command" => "包装命令",
        "Pre-launch Command" => "启动前命令",
        "Runtime List" => "已安装的运行时",
        "Version" => "版本",
        "Vendor" => "发行商",
        "Architecture" => "架构",
        "Path" => "路径",
        "Status" => "状态",
        "Snapshot" => "快照",
        "Cache" => "缓存",
        "Portable Mode" => "便携模式",
        "Cloud Account" => "云账户",
        "Plan" => "订阅方案",
        "Minecraft Options" => "游戏选项",
        "Server List" => "服务器列表",
        "Resource Packs" => "资源包",
        "Command History" => "命令历史",
        "Creative Hotbars" => "创造模式快捷栏",
        "Screenshots" => "截图",
        "Instance Metadata" => "实例信息",
        "Worlds" => "世界",
        "Config" => "配置文件",
        "Thin Backup" => "精简备份",
        "Crash Report" => "崩溃报告",
        "Diagnostic Data" => "诊断数据",
        "Crash Analyzer" => "崩溃分析",
        "Release Notes" => "更新日志",
        "Signature Verification" => "签名验证",
        "Atomic Update" => "原子更新",
        "Last Known Good" => "上次正常版本",
        "Auto Rollback" => "自动回滚",
        "Launcher Safe Mode" => "启动器安全模式",
        _ => label,
    };
    public void Dispose()
    {
        _disposed = true; CancelSettingsTransfer(); CancelJavaInventory(); _diagnosticStop.Cancel(); CancelManagementRead(); CancelOnlineContent(); CancelModRemovalPreview(); _updateStop.Cancel(); _updateCheckStop?.Dispose(); _intents.IntentEmitted -= OnIntent; _shell.Renderer.FramePreparing -= OnFrame;
    }
}
