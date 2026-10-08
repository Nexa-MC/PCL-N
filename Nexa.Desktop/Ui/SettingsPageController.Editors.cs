using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId ArgumentAdd = XsrSemanticId.Parse("ui.settings.argument.add");
    private static readonly XsrSemanticId ArgumentRemove = XsrSemanticId.Parse("ui.settings.argument.remove");
    private sealed record ShiftSelector(Editor Editor, XsrUiEntityId Track, Dictionary<XsrUiEntityId, string> Options);
    private sealed class ArgumentEditor(SettingsCatalogEntry entry, XsrUiEntityId rows)
    {
        public SettingsCatalogEntry Entry { get; } = entry;
        public XsrUiEntityId Rows { get; } = rows;
        public List<XsrUiEntityId> Inputs { get; } = [];
        public bool Initialized { get; set; }
    }
    private readonly Dictionary<XsrUiEntityId, ShiftSelector> _selectors = [];
    private readonly Dictionary<XsrUiEntityId, ArgumentEditor> _argumentEditors = [];
    private readonly Dictionary<XsrUiEntityId, (ArgumentEditor Editor, int Index)> _argumentActions = [];

    private XsrUiEntityId ActionButton(XsrUiEntityId parent, string name, string text, XsrSemanticId command, double width)
    {
        var button = Element(parent, name, XsrUiSemanticRole.Button, text, width, 34);
        _shell.Tree.SetComponent(button, new XsrUiText(text));
        _shell.Tree.SetComponent(button, new XsrUiInput { Focusable = true, Clickable = true });
        _shell.Tree.SetComponent(button, new XsrUiCommandBinding(command));
        Style(button, new(242, 245, 249), Blue, 9, 12, 500);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(button)!.TextAlignment = XsrUiTextAlignment.Center;
        return button;
    }

    private void BuildShiftSelector(XsrUiEntityId parent, SettingsCatalogEntry entry)
    {
        var track = Stack(parent, "SettingsSelector." + entry.SettingKey, XsrUiOrientation.Horizontal, 0);
        _shell.Tree.SetComponent(track, new XsrUiSemantic(XsrUiSemanticRole.RadioGroup, DisplayLabel(entry.Label)));
        Style(track, new(241, 245, 250), Ink, 9);
        _shell.Tree.GetComponent<XsrUiElement>(track)!.Padding = new(3, 3, 3, 3);
        _shell.Tree.GetComponent<XsrUiElement>(track)!.HorizontalAlignment = XsrUiAlignment.End;
        var thumb = Element(track, "SettingsSelectorThumb." + entry.SettingKey, XsrUiSemanticRole.None, null);
        _shell.Tree.GetComponent<XsrUiElement>(thumb)!.IsVisible = false;
        Style(thumb, White, Ink, 7);
        _shell.Tree.SetComponent(thumb, new XsrUiTransition());
        _shell.Tree.SetComponent(track, new XsrUiSegmentedTrack(thumb));
        _shell.Tree.SetComponent(track, new XsrUiScroll());
        _shell.Tree.SetComponent(track, new XsrUiScrollGesture());
        Dictionary<XsrUiEntityId, string> options = [];
        string[] values = entry.SettingKey == "general.region" ? ["auto", "follow-language", "zh-CN", "zh-TW", "en-US"]
            : entry.Definition!.Kind == SettingsValueKind.Boolean ? entry.InvertBoolean ? ["false", "true"] : ["true", "false"]
            : entry.Definition.Choices.Split('|');
        double width = 0;
        foreach (string value in values)
        {
            string label = value switch { "true" => "开启", "false" => "关闭", "fullscreen" => "全屏", "windowed" => "窗口", "auto" => "跟随系统", "zh-Hans" => "简体中文", "zh-Hant" => "繁體中文", "en" => "English", _ => value };
            if (entry.InvertBoolean) label = value == "true" ? "关闭" : "开启";
            if (entry.SettingKey == "general.region") label = value switch
            { "auto" => "系统区域", "follow-language" => "界面语言", "zh-CN" => "中国大陆", "zh-TW" => "中国台湾", "en-US" => "美国", _ => value };
            if (entry.SettingKey is "network.game-source" or "network.resource-source") label = value switch
            { "official-first" => "官方优先", "mirrors-first" => "镜像优先", "official-only" => "仅官方", "follow-request" => "跟随页面", _ => value };
            if (entry.SettingKey == "general.startup-page") label = value switch
            { "launch" => "启动", "install" => "安装", "resources" => "资源", "settings" => "设置", "java" => "Java", "storage" => "存储", "about" => "关于", "tasks" => "任务", _ => value };
            if (entry.SettingKey == "network.ip-stack") label = value switch
            { "auto" => "系统顺序", "ipv4" => "IPv4 优先", "ipv6" => "IPv6 优先", _ => value };
            if (entry.SettingKey == "game.gpu-preference") label = value switch
            { "auto" => "跟随系统", "secondary" => "次级 GPU（Mesa）", _ => value };
            if (entry.SettingKey == "game.renderer") label = value switch
            { "auto" => "跟随系统", "mesa-software" => "Mesa 软件渲染", _ => value };
            if (entry.SettingKey == "game.process-priority") label = value switch
            { "normal" => "正常", "below-normal" => "较低", "above-normal" => "较高", "high" => "高", "real-time" => "实时", _ => value };
            if (entry.SettingKey == "game.launcher-visibility") label = value switch
            { "keep" => "保持显示", "minimize" => "最小化", "hide" => "隐藏后恢复", "hide-and-close" => "游戏退出后关闭", _ => value };
            if (entry.SettingKey == "game.default-isolation") label = value switch
            { "none" => "不隔离", "loaders" => "加载器版本", "non-release" => "非正式版", "loaders-or-non-release" => "加载器与非正式版", "all" => "全部版本", _ => value };
            if (entry.SettingKey == "updates.channel") label = value switch
            { "build" => "跟随当前版本", "stable" => "正式版", "alpha" => "Alpha", "beta" => "Beta", "ci" => "CI", _ => value };
            if (entry.SettingKey == "diagnostics.log-level") label = value switch
            { "auto" => "自动", "0" => "仅错误", "1" => "警告", "2" => "信息", "3" => "调试", "4" => "详细跟踪", _ => value };
            if (entry.SettingKey == "appearance.theme-mode") label = value switch
            { "2" => "跟随系统", "0" => "浅色", "1" => "深色", _ => value };
            if (entry.SettingKey == "appearance.accent") label = value switch
            { "blue" => "蓝色", "purple" => "紫色", "green" => "绿色", "orange" => "橙色", _ => value };
            if (entry.SettingKey == "java.vendor") label = value switch
            { "" => "自动", "EclipseTemurin" => "Temurin", "IbmSemeru" => "IBM Semeru", "GraalVmCommunity" => "GraalVM", "OpenJdk" => "OpenJDK", "TencentKona" => "腾讯 Kona", "Dragonwell" => "龙井 Java", "Unknown" => "其他", _ => value };
            bool nativeName = entry.SettingKey == "general.language" && value != "auto";
            double optionWidth = Math.Max(48, (nativeName ? label : _shell.Renderer.LocalizeText(label)).Sum(character => character > 127 ? 12 : 7) + 24);
            var option = RadioOption(track, "SettingsOption." + entry.SettingKey + "." + value, label, Choice, optionWidth);
            if (nativeName) DesktopLiteralText.Preserve(_shell.Tree, option);
            _shell.Tree.SetComponent(option, new XsrUiSelection());
            options[option] = value; width += optionWidth;
        }
        _shell.Tree.GetComponent<XsrUiElement>(track)!.Width = width;
        var editor = new Editor(entry, default, options.Keys.First(), default);
        _editors[editor.Button] = editor;
        _selectors[editor.Button] = new(editor, track, options);
    }

    private void UpdateShiftSelectors()
    {
        foreach (var selector in _selectors.Values)
        {
            string raw = _values?.Values.FirstOrDefault(item => item.Key == selector.Editor.Entry.SettingKey)?.Value.Value ?? selector.Options.Values.First();
            if (selector.Editor.Entry.SettingKey == "general.region")
            {
                if (raw == "ui-language") raw = "follow-language";
                if (!selector.Options.Values.Contains(raw, StringComparer.Ordinal))
                {
                    // Existing custom cultures stay reachable; the form never substitutes a preset.
                    try
                    {
                        string name = System.Globalization.CultureInfo.GetCultureInfo(raw).NativeName;
                        var custom = RadioOption(selector.Track, "SettingsOption.general.region." + raw, name, Choice, 48);
                        DesktopLiteralText.Preserve(_shell.Tree, custom);
                        _shell.Tree.SetComponent(custom, new XsrUiSelection());
                        selector.Options[custom] = raw;
                        _shell.Tree.MarkDirty(selector.Track, XsrUiDirtyKinds.Layout);
                    }
                    catch (System.Globalization.CultureNotFoundException) { }
                }
            }
            foreach (var option in selector.Options)
            {
                bool selected = option.Value == raw;
                _choices[option.Key] = (selector.Editor, option.Value);
                _shell.Tree.GetComponent<XsrUiSelection>(option.Key)!.IsSelected = selected;
                Style(option.Key, XsrUiColor.Transparent, selected ? Blue : Ink, 7, 12, selected ? 600 : 450);
                _shell.Tree.GetComponent<XsrUiVisualStyle>(option.Key)!.TextAlignment = XsrUiTextAlignment.Center;
                if (selected) _shell.Tree.GetComponent<XsrUiSegmentedTrack>(selector.Track)!.Selected = option.Key;
            }
            double width = 0;
            foreach (var option in selector.Options.Keys)
            {
                var text = _shell.Tree.GetComponent<XsrUiText>(option)!;
                double desired = Math.Max(48, (text.Localize ? _shell.Renderer.LocalizeText(text.Content) : text.Content).Sum(character => character > 127 ? 12 : 7) + 24);
                var element = _shell.Tree.GetComponent<XsrUiElement>(option)!;
                if (element.Width != desired) { element.Width = desired; _shell.Tree.MarkDirty(option, XsrUiDirtyKinds.Layout); }
                width += desired;
            }
            _shell.Tree.GetComponent<XsrUiElement>(selector.Track)!.Width = width;
        }
    }

    private void BuildArgumentEditor(XsrUiEntityId parent, SettingsCatalogEntry entry)
    {
        var panel = Stack(parent, "SettingsArguments." + entry.SettingKey, XsrUiOrientation.Horizontal, 8);
        _shell.Tree.GetComponent<XsrUiElement>(panel)!.Weight = 1;
        _shell.Tree.GetComponent<XsrUiElement>(panel)!.Padding = new(0, 6, 0, 6);
        var rows = Stack(panel, "SettingsArgumentRows." + entry.SettingKey, XsrUiOrientation.Vertical, 6);
        _shell.Tree.GetComponent<XsrUiElement>(rows)!.Weight = 1;
        var actions = Stack(panel, "SettingsArgumentActions", XsrUiOrientation.Vertical, 6);
        _shell.Tree.GetComponent<XsrUiElement>(actions)!.VerticalAlignment = XsrUiAlignment.Start;
        var apply = ActionButton(actions, "SettingsEdit." + entry.SettingKey, "应用", Edit, 44);
        var add = ActionButton(actions, "SettingsArgumentAdd." + entry.SettingKey, "添加", ArgumentAdd, 44);
        var editor = new ArgumentEditor(entry, rows);
        _argumentEditors[apply] = editor;
        _argumentActions[add] = (editor, -1);
        _editors[apply] = new(entry, default, apply, default);
        BuildArgumentRows(editor, [""]);
    }

    private string[] ReadArgumentDrafts(ArgumentEditor editor) => editor.Inputs
        .Select(input => _shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft()).ToArray();

    private void BuildArgumentRows(ArgumentEditor editor, IReadOnlyList<string> values)
    {
        foreach (var key in _argumentActions.Where(pair => pair.Value.Editor == editor && pair.Value.Index >= 0).Select(pair => pair.Key).ToArray()) _argumentActions.Remove(key);
        foreach (var child in _shell.Tree.Children(editor.Rows).ToArray()) _shell.Tree.Destroy(child);
        editor.Inputs.Clear();
        for (int i = 0; i < Math.Max(1, values.Count); i++)
        {
            var row = Stack(editor.Rows, "SettingsArgumentRow", XsrUiOrientation.Horizontal, 6);
            var input = Element(row, "SettingsArgument." + editor.Entry.SettingKey + "." + i,
                XsrUiSemanticRole.TextInput, editor.Entry.Label + " " + (i + 1), height: 30);
            var layout = _shell.Tree.GetComponent<XsrUiElement>(input)!; layout.Weight = 1; layout.Padding = new(8, 0, 8, 0);
            _shell.Tree.SetComponent(input, new XsrUiTextInput { Placeholder = "输入参数" });
            _shell.Tree.SetComponent(input, new XsrUiInput { Focusable = true, Clickable = true });
            Style(input, new(244, 247, 251), Ink, 7, 12);
            _shell.Renderer.SetTextInputValue(input, i < values.Count ? values[i] : "");
            editor.Inputs.Add(input);
            var remove = ActionButton(row, "SettingsArgumentRemove." + editor.Entry.SettingKey + "." + i, "×", ArgumentRemove, 28);
            _shell.Tree.GetComponent<XsrUiSemantic>(remove)!.Label = "删除参数 " + (i + 1);
            _argumentActions[remove] = (editor, i);
        }
        _shell.Tree.MarkDirty(editor.Rows, XsrUiDirtyKinds.Layout);
    }

    private void UpdateArgumentEditors()
    {
        foreach (var editor in _argumentEditors.Values)
        {
            if (editor.Initialized || _values is null) continue;
            var value = _values.Values.First(item => item.Key == editor.Entry.SettingKey);
            BuildArgumentRows(editor, value.ArgumentRows);
            editor.Initialized = true;
        }
    }

    private void HandleArgumentIntent(DesktopUiIntent intent)
    {
        if (!_argumentActions.TryGetValue(intent.Source, out var action)) return;
        var values = ReadArgumentDrafts(action.Editor).ToList();
        if (intent.Command == ArgumentAdd) values.Add("");
        else if (action.Index >= 0 && action.Index < values.Count) values.RemoveAt(action.Index);
        BuildArgumentRows(action.Editor, values);
        action.Editor.Initialized = true;
        _shell.Renderer.Focus(action.Editor.Inputs[intent.Command == ArgumentAdd ? action.Editor.Inputs.Count - 1 : Math.Min(action.Index, action.Editor.Inputs.Count - 1)]);
    }
}
