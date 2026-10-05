using System.Text;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId HookAdd = XsrSemanticId.Parse("ui.settings.hook.add");
    private static readonly XsrSemanticId HookRemove = XsrSemanticId.Parse("ui.settings.hook.remove");
    private static readonly XsrSemanticId HookPage = XsrSemanticId.Parse("ui.settings.hook.page");
    private const int HookLinesPerPage = 32;
    private sealed record HookLine(string Text, string Separator);
    private sealed record HookDraftSnapshot(string Command, string Committed, int Page);
    private sealed record HookInheritanceAttempt(string Key, string? Instance, Task<XsrResult> Completion);
    private sealed class HookEditor(SettingsCatalogEntry entry, XsrUiEntityId rows)
    {
        public SettingsCatalogEntry Entry { get; } = entry;
        public XsrUiEntityId Rows { get; } = rows;
        public List<HookLine> Lines { get; set; } = [new("", "")];
        public List<(XsrUiEntityId Entity, int Index, string Original, string Presented)> Inputs { get; } = [];
        public string Committed { get; set; } = "";
        public bool Initialized { get; set; }
        public int Page { get; set; }
    }
    private readonly Dictionary<XsrUiEntityId, HookEditor> _hookEditors = [];
    private readonly Dictionary<XsrUiEntityId, (HookEditor Editor, int Index)> _hookActions = [];
    private HookInheritanceAttempt? _hookInheritance;
    internal bool SettingsWritePending => _writing is not null;

    private void BuildHookEditor(XsrUiEntityId parent, SettingsCatalogEntry entry)
    {
        var panel = Stack(parent, "SettingsHook." + entry.SettingKey, XsrUiOrientation.Horizontal, 8);
        _shell.Tree.GetComponent<XsrUiElement>(panel)!.Weight = 1;
        var rows = Stack(panel, "SettingsHookRows." + entry.SettingKey, XsrUiOrientation.Vertical, 6);
        _shell.Tree.GetComponent<XsrUiElement>(rows)!.Weight = 1;
        var apply = ActionButton(panel, "SettingsEdit." + entry.SettingKey, "应用", Edit, 44);
        _shell.Tree.GetComponent<XsrUiElement>(apply)!.VerticalAlignment = XsrUiAlignment.Start;
        var editor = new HookEditor(entry, rows);
        _hookEditors[apply] = editor;
        _editors[apply] = new(entry, default, apply, default);
        BuildHookRows(editor);
    }

    private static List<HookLine> SplitHookLines(string command)
    {
        List<HookLine> lines = [];
        int start = 0;
        for (int index = 0; index < command.Length; index++)
        {
            if (command[index] is not ('\r' or '\n')) continue;
            int textEnd = index;
            if (command[index] == '\r' && index + 1 < command.Length && command[index + 1] == '\n') index++;
            lines.Add(new(command[start..textEnd], command[textEnd..(index + 1)]));
            start = index + 1;
        }
        lines.Add(new(command[start..], ""));
        return lines;
    }

    private void CaptureHookLines(HookEditor editor)
    {
        foreach (var input in editor.Inputs)
        {
            string text = _shell.Tree.GetComponent<XsrUiTextInput>(input.Entity)!.ReadDraft();
            // Keep any existing non-display control verbatim unless the user edits this line.
            editor.Lines[input.Index] = editor.Lines[input.Index] with { Text = text == input.Presented ? input.Original : text };
        }
    }

    private string ReadHookDraft(HookEditor editor)
    {
        CaptureHookLines(editor);
        StringBuilder command = new();
        foreach (var line in editor.Lines) command.Append(line.Text).Append(line.Separator);
        return command.ToString();
    }

    private Dictionary<string, HookDraftSnapshot> CaptureHookDrafts(bool navigating)
        => navigating ? [] : _hookEditors.Values.ToDictionary(editor => editor.Entry.Id,
            editor => new HookDraftSnapshot(ReadHookDraft(editor), editor.Committed, editor.Page));

    private void RestoreHookDrafts(IReadOnlyDictionary<string, HookDraftSnapshot> drafts)
    {
        foreach (var editor in _hookEditors.Values)
            if (drafts.TryGetValue(editor.Entry.Id, out var draft))
            {
                editor.Lines = SplitHookLines(draft.Command); editor.Committed = draft.Committed;
                editor.Page = draft.Page; editor.Initialized = true; BuildHookRows(editor);
            }
    }

    private void UpdateHookEditors()
    {
        foreach (var editor in _hookEditors.Values)
        {
            string? raw = _values?.Values.FirstOrDefault(value => value.Key == editor.Entry.SettingKey)?.Value.Value;
            if (raw is null) continue;
            if (!editor.Initialized || raw != editor.Committed && ReadHookDraft(editor) == editor.Committed)
            {
                editor.Lines = SplitHookLines(raw); editor.Initialized = true; BuildHookRows(editor);
            }
            editor.Committed = raw;
        }
    }

    private void TrackHookInheritance(string key, Task<XsrResult> completion)
    {
        if (_hookEditors.Values.Any(editor => editor.Entry.SettingKey == key))
            _hookInheritance = new(key, _instance, completion);
    }

    private void UpdateHookInheritance()
    {
        if (_hookInheritance is not { Completion.IsCompleted: true } attempt) return;
        _hookInheritance = null;
        if (!attempt.Completion.IsCompletedSuccessfully || !attempt.Completion.Result.IsSuccess
            || attempt.Instance != _instance) return;
        foreach (var editor in _hookEditors.Values.Where(editor => editor.Entry.SettingKey == attempt.Key))
            editor.Initialized = false;
        // The write may finish after an effective-value query already completed. Replace
        // the draft now and refresh the committed value even when that revision was seen.
        UpdateHookEditors();
        _revision = -1;
    }

    private void BuildHookRows(HookEditor editor)
    {
        foreach (var key in _hookActions.Where(pair => pair.Value.Editor == editor).Select(pair => pair.Key).ToArray()) _hookActions.Remove(key);
        foreach (var child in _shell.Tree.Children(editor.Rows).ToArray()) _shell.Tree.Destroy(child);
        editor.Inputs.Clear();
        editor.Page = Math.Clamp(editor.Page, 0, (editor.Lines.Count - 1) / HookLinesPerPage);
        int start = editor.Page * HookLinesPerPage;
        for (int index = start; index < Math.Min(editor.Lines.Count, start + HookLinesPerPage); index++)
        {
            var row = Stack(editor.Rows, "SettingsHookRow", XsrUiOrientation.Horizontal, 6);
            var input = Element(row, "SettingsHookLine." + editor.Entry.SettingKey + "." + index,
                XsrUiSemanticRole.TextInput, "命令行 " + (index + 1), height: 30);
            var layout = _shell.Tree.GetComponent<XsrUiElement>(input)!; layout.Weight = 1; layout.Padding = new(8, 0, 8, 0);
            _shell.Tree.SetComponent(input, new XsrUiTextInput { MaximumLength = 32768, PreserveTabs = true, Placeholder = "输入命令；空行也会保留" });
            _shell.Tree.SetComponent(input, new XsrUiInput { Focusable = true, Clickable = true });
            Style(input, new(244, 247, 251), Ink, 7, 12);
            _shell.Renderer.SetTextInputValue(input, editor.Lines[index].Text);
            editor.Inputs.Add((input, index, editor.Lines[index].Text, _shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft()));
            var remove = ActionButton(row, "SettingsHookRemove." + editor.Entry.SettingKey + "." + index, "×", HookRemove, 28);
            _shell.Tree.GetComponent<XsrUiSemantic>(remove)!.Label = "删除命令行 " + (index + 1);
            _hookActions[remove] = (editor, index);
        }
        var actions = Stack(editor.Rows, "SettingsHookActions", XsrUiOrientation.Horizontal, 6);
        var add = ActionButton(actions, "SettingsHookAdd." + editor.Entry.SettingKey, "添加行", HookAdd, 72);
        _hookActions[add] = (editor, -1);
        if (editor.Lines.Count > HookLinesPerPage)
        {
            var previous = ActionButton(actions, "SettingsHookPrevious." + editor.Entry.SettingKey, "上一页", HookPage, 72);
            var next = ActionButton(actions, "SettingsHookNext." + editor.Entry.SettingKey, "下一页", HookPage, 72);
            _hookActions[previous] = (editor, -1); _hookActions[next] = (editor, 1);
            Text(actions, (editor.Page + 1) + " / " + ((editor.Lines.Count - 1) / HookLinesPerPage + 1), 12, Muted, height: 30);
        }
        _shell.Tree.MarkDirty(editor.Rows, XsrUiDirtyKinds.Layout);
    }

    private static bool IsHookIntent(XsrSemanticId command) => command == HookAdd || command == HookRemove || command == HookPage;

    private void HandleHookIntent(DesktopUiIntent intent)
    {
        if (!_hookActions.TryGetValue(intent.Source, out var action)) return;
        var editor = action.Editor; CaptureHookLines(editor);
        if (intent.Command == HookPage) editor.Page += action.Index;
        else if (intent.Command == HookAdd)
        {
            string separator = editor.Lines.FirstOrDefault(line => line.Separator.Length > 0)?.Separator ?? "\n";
            editor.Lines[^1] = editor.Lines[^1] with { Separator = separator };
            editor.Lines.Add(new("", "")); editor.Page = (editor.Lines.Count - 1) / HookLinesPerPage;
        }
        else if (action.Index >= 0 && action.Index < editor.Lines.Count)
        {
            editor.Lines.RemoveAt(action.Index);
            if (editor.Lines.Count == 0) editor.Lines.Add(new("", ""));
            editor.Lines[^1] = editor.Lines[^1] with { Separator = "" };
        }
        editor.Initialized = true; BuildHookRows(editor);
        var focus = intent.Command == HookAdd ? editor.Inputs[^1].Entity : editor.Inputs[0].Entity;
        _shell.Renderer.Focus(focus);
    }
}
