using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private readonly Dictionary<XsrUiEntityId, (Editor Editor, XsrUiEntityId Auto, XsrUiEntityId Manual)> _autoModes = [];

    private XsrUiEntityId RefreshIcon(XsrUiEntityId parent, string name, XsrSemanticId command)
    {
        var button = Element(parent, name, XsrUiSemanticRole.Button, "刷新", 36, 36);
        _shell.Tree.SetComponent(button, new XsrUiInput { Focusable = true, Clickable = true });
        _shell.Tree.SetComponent(button, new XsrUiCommandBinding(command));
        _shell.Tree.SetComponent(button, new XsrUiImage("pcl/refresh"));
        Style(button, new(242, 245, 249), Blue, 10);
        return button;
    }

    private void BuildAutomaticMode(XsrUiEntityId parent, Editor editor)
    {
        var track = Stack(parent, "SettingsMode." + editor.Entry.SettingKey, XsrUiOrientation.Horizontal, 0);
        _shell.Tree.GetComponent<XsrUiElement>(track)!.Width = 112;
        _shell.Tree.SetComponent(track, new XsrUiSemantic(XsrUiSemanticRole.RadioGroup, "分配方式"));
        Style(track, new(241, 245, 250), Ink, 9);
        var thumb = Element(track, "SettingsModeThumb", XsrUiSemanticRole.None, null);
        _shell.Tree.GetComponent<XsrUiElement>(thumb)!.IsVisible = false;
        Style(thumb, White, Ink, 7); _shell.Tree.SetComponent(thumb, new XsrUiTransition());
        _shell.Tree.SetComponent(track, new XsrUiSegmentedTrack(thumb));
        var automatic = RadioOption(track, "SettingsAuto." + editor.Entry.SettingKey, "自动", Choice, 56);
        var manual = RadioOption(track, "SettingsManual." + editor.Entry.SettingKey, "手动", Choice, 56);
        _choices[automatic] = (editor, "auto"); _choices[manual] = (editor, "__manual__");
        _autoModes[track] = (editor, automatic, manual);
    }

    private void UpdateAutomaticModes()
    {
        foreach (var (track, mode) in _autoModes)
        {
            var value = _values?.Values.FirstOrDefault(item => item.Key == mode.Editor.Entry.SettingKey)?.Value;
            bool auto = value?.Mode == SettingsOverrideMode.Auto || mode.Editor.Entry.SettingKey == "java.runtime" && string.IsNullOrWhiteSpace(value?.Value);
            var selected = auto ? mode.Auto : mode.Manual;
            _shell.Tree.GetComponent<XsrUiSegmentedTrack>(track)!.Selected = selected;
            _shell.Tree.GetComponent<XsrUiSelection>(mode.Auto)!.IsSelected = auto;
            _shell.Tree.GetComponent<XsrUiSelection>(mode.Manual)!.IsSelected = !auto;
        }
    }

    private XsrUiEntityId ToggleControl(XsrUiEntityId parent, string name, string label, bool value,
        XsrSemanticId command, bool checkBox = false, double? width = null)
    {
        var control = Element(parent, name, checkBox ? XsrUiSemanticRole.CheckBox : XsrUiSemanticRole.Switch,
            label, width ?? (checkBox ? 100 : 52), 32);
        _shell.Tree.SetComponent(control, new XsrUiInput { Focusable = true, Clickable = true });
        _shell.Tree.SetComponent(control, new XsrUiToggle(value));
        _shell.Tree.SetComponent(control, new XsrUiCommandBinding(command));
        if (checkBox) _shell.Tree.SetComponent(control, new XsrUiText(label));
        Style(control, XsrUiColor.Transparent, checkBox ? Ink : Blue, 0, 13);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(control)!.Border = Blue;
        return control;
    }

    private XsrUiEntityId RadioOption(XsrUiEntityId parent, string name, string label, XsrSemanticId command, double width)
    {
        var control = Element(parent, name, XsrUiSemanticRole.RadioButton, label, width, 34);
        _shell.Tree.SetComponent(control, new XsrUiText(label));
        _shell.Tree.SetComponent(control, new XsrUiInput { Focusable = true, Clickable = true });
        _shell.Tree.SetComponent(control, new XsrUiCommandBinding(command));
        _shell.Tree.SetComponent(control, new XsrUiSelection());
        Style(control, XsrUiColor.Transparent, Ink, 7, 12);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(control)!.TextAlignment = XsrUiTextAlignment.Center;
        return control;
    }

    private void BuildBinaryChoice(XsrUiEntityId parent, string name, string label, bool value, Action<bool> select)
    {
        var track = Stack(parent, name, XsrUiOrientation.Horizontal, 0);
        _shell.Tree.SetComponent(track, new XsrUiSemantic(XsrUiSemanticRole.RadioGroup, label));
        _shell.Tree.GetComponent<XsrUiElement>(track)!.Width = 118;
        _shell.Tree.GetComponent<XsrUiElement>(track)!.Padding = new(3, 3, 3, 3);
        Style(track, new(241, 245, 250), Ink, 9);
        var thumb = Element(track, name + ".Thumb", XsrUiSemanticRole.None, null);
        _shell.Tree.GetComponent<XsrUiElement>(thumb)!.IsVisible = false;
        Style(thumb, White, Ink, 7); _shell.Tree.SetComponent(thumb, new XsrUiTransition());
        var selection = new XsrUiSegmentedTrack(thumb); _shell.Tree.SetComponent(track, selection);
        foreach (bool active in new[] { true, false })
        {
            var option = RadioOption(track, name + (active ? ".true" : ".false"), active ? "开启" : "关闭", ManagementAction, 56);
            _shell.Tree.GetComponent<XsrUiSelection>(option)!.IsSelected = active == value;
            Style(option, XsrUiColor.Transparent, active == value ? Blue : Ink, 7, 12, active == value ? 600 : 450);
            _shell.Tree.GetComponent<XsrUiVisualStyle>(option)!.TextAlignment = XsrUiTextAlignment.Center;
            if (active == value) selection.Selected = option;
            RegisterContentAction(option, () => { if (active != value) select(active); });
        }
    }

    private XsrUiEntityId FormGroup(XsrUiEntityId parent, string name, string? label = null)
    {
        var group = Stack(parent, name, XsrUiOrientation.Vertical, 8);
        if (label is not null) Text(group, label, 14, Muted, 24, 500);
        var surface = Stack(group, name + ".Surface", XsrUiOrientation.Vertical, 0);
        Style(surface, White, Ink, 14);
        var body = Stack(surface, name + ".Body", XsrUiOrientation.Vertical, 4);
        _shell.Tree.GetComponent<XsrUiElement>(body)!.Padding = new(16, 8, 16, 8);
        return body;
    }

    private static string InstanceSettingGroup(SettingsCatalogEntry entry) => entry.SettingKey switch
    {
        "game.memory" or "java.runtime" or "java.vendor" or "java.auto-install" => "Java 与内存",
        "game.server" => "服务器",
        "game.jvm" or "game.arguments" => "高级参数",
        _ => "启动与窗口",
    };
}
