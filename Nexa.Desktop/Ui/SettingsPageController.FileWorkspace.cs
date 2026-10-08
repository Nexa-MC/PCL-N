using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private string _workspaceArea = "config", _workspaceDirectory = "";
    private string? _workspaceInstance, _workspaceError;
    private Task<XsrResult<InstanceFileListing>>? _workspaceListRead;
    private Task<XsrResult<InstanceFileDocument>>? _workspaceDocumentRead;
    private Task<XsrResult<InstanceFileSavePreview>>? _workspacePreviewRead;
    private InstanceFileListing? _workspaceListing;
    private InstanceFileDocument? _workspaceDocument;
    private InstanceFileSavePreview? _workspacePreview;
    private CancellationTokenSource? _workspaceStop;
    private string _workspaceDraft = "";
    private int _workspaceListPage, _workspaceTextPage, _workspaceLine;
    private XsrUiEntityId _workspaceLineEditor, _workspaceLineNumber;

    private void CancelFileWorkspace()
    {
        _workspaceStop?.Cancel(); _workspaceStop?.Dispose(); _workspaceStop = null;
        _workspaceListRead = null; _workspaceDocumentRead = null; _workspacePreviewRead = null;
        _workspaceListing = null; _workspaceDocument = null; _workspacePreview = null; _workspaceError = null;
        _workspaceLineEditor = _workspaceLineNumber = default; _workspaceDraft = "";
    }

    private void ChangeWorkspace(string area, string relative)
    {
        CancelFileWorkspace(); _workspaceArea = area; _workspaceDirectory = relative;
        _workspaceListPage = _workspaceTextPage = _workspaceLine = 0; BuildSections(true);
    }

    private void UpdateFileWorkspace()
    {
        if (_selected != "files" || _instance is null) return;
        if (_workspaceInstance != _instance)
        {
            CancelFileWorkspace(); _workspaceInstance = _instance; _workspaceArea = "config"; _workspaceDirectory = "";
            _workspaceListPage = _workspaceTextPage = _workspaceLine = 0;
        }
        bool changed = false;
        if (_workspaceListRead is { IsCompleted: true } list)
        {
            _workspaceListRead = null;
            if (PendingQuery.Succeeded(list)) _workspaceListing = list.Result.Value; else _workspaceError = "无法读取此目录，请检查访问权限。";
            changed = true;
        }
        if (_workspaceDocumentRead is { IsCompleted: true } document)
        {
            _workspaceDocumentRead = null;
            if (PendingQuery.Succeeded(document)) { _workspaceDocument = document.Result.Value; _workspaceDraft = _workspaceDocument!.Text; _workspaceLine = _workspaceTextPage = 0; }
            else _workspaceError = document.IsCompletedSuccessfully ? document.Result.Error?.Message ?? "文件不是受支持的有界文本。" : "无法读取此文本文件。";
            changed = true;
        }
        if (_workspacePreviewRead is { IsCompleted: true } preview)
        {
            _workspacePreviewRead = null;
            if (PendingQuery.Succeeded(preview)) _workspacePreview = preview.Result.Value;
            else _workspaceError = preview.IsCompletedSuccessfully ? preview.Result.Error?.Message ?? "配置预览未完成。" : "配置预览未完成。";
            changed = true;
        }
        if (_workspaceListing is null && _workspaceListRead is null && _workspaceError is null && _queries.TryResolve(InstanceFileWorkspaceContract.List, out var route))
        {
            _workspaceStop ??= new();
            _workspaceListRead = _queries.QueryAsync<InstanceFileListQuery, InstanceFileListing>(route, new(_instance, _workspaceArea, _workspaceDirectory), cancellationToken: _workspaceStop.Token).AsTask();
            WakeOnPlatformCompletion(_workspaceListRead);
        }
        if (changed) { CaptureWorkspaceLine(); BuildSections(true); }
    }

    private void BuildInstanceFiles()
    {
        _workspaceLineEditor = _workspaceLineNumber = default;
        var navigation = Stack(_sections, "FileWorkspaceNavigation", XsrUiOrientation.Horizontal, 10);
        foreach (var (area, label) in new[] { ("config", "配置文件"), ("logs", "日志"), ("crash-reports", "崩溃记录"), ("other", "其它文件（只读）") })
            ManagementButton(navigation, label, () => ChangeWorkspace(area, ""), 96);
        if (_workspaceError is { } error) Text(_sections, error, 13, Muted, 32);
        if (_workspaceDocument is { } document) { BuildWorkspaceDocument(document); return; }
        if (_workspaceListing is not { } listing) { Text(_sections, "正在读取受限文件工作区…", 13, Muted, 30); return; }
        var location = Stack(_sections, "FileWorkspaceLocation", XsrUiOrientation.Horizontal, 10);
        var path = Text(location, listing.Directory, 12, Muted, 32); DesktopLiteralText.Preserve(_shell.Tree, path); _shell.Tree.GetComponent<XsrUiElement>(path)!.Weight = 1;
        ManagementButton(location, "打开文件夹", () => OpenContentDirectory(listing.Directory), 104);
        if (_workspaceDirectory.Length > 0) ManagementButton(location, "上级目录", () => ChangeWorkspace(_workspaceArea, _workspaceDirectory.Contains('/') ? _workspaceDirectory[.._workspaceDirectory.LastIndexOf('/')] : ""), 96);
        if (!listing.Complete) Text(_sections, "部分条目超过预算或是链接，已省略。", 12, Muted, 28);
        if (listing.Entries.Count == 0) Text(_sections, "此目录中还没有文件。", 13, Muted, 28);
        int pages = Math.Max(1, (listing.Entries.Count + 24) / 25); _workspaceListPage = Math.Clamp(_workspaceListPage, 0, pages - 1);
        foreach (var entry in listing.Entries.Skip(_workspaceListPage * 25).Take(25))
        {
            var row = Stack(_sections, "FileWorkspaceRow." + entry.Name, XsrUiOrientation.Horizontal, 12);
            var name = Text(row, entry.Name, 13, Ink, 38); DesktopLiteralText.Preserve(_shell.Tree, name); _shell.Tree.GetComponent<XsrUiElement>(name)!.Weight = 1;
            Text(row, entry.IsDirectory ? "文件夹" : FormatContentSize(entry.Size ?? 0), 12, Muted, 38);
            ManagementButton(row, entry.IsDirectory ? "浏览" : "查看文本", () =>
            {
                string relative = _workspaceDirectory.Length > 0 ? _workspaceDirectory + "/" + entry.Name : entry.Name;
                if (entry.IsDirectory) { ChangeWorkspace(_workspaceArea, relative); return; }
                if (_workspaceDocumentRead is not null || !_queries.TryResolve(InstanceFileWorkspaceContract.Read, out var route)) return;
                _workspaceError = null; _workspaceStop ??= new();
                _workspaceDocumentRead = _queries.QueryAsync<InstanceFileReadQuery, InstanceFileDocument>(route,
                    new(_instance!, _workspaceArea, relative, entry.Size ?? 0, entry.ModifiedUtcTicks), cancellationToken: _workspaceStop.Token).AsTask();
                WakeOnPlatformCompletion(_workspaceDocumentRead);
            }, 96);
        }
        if (pages > 1) ManagementButton(_sections, $"{_workspaceListPage + 1}/{pages} · " + _shell.Renderer.LocalizeText("下一页"), () => { _workspaceListPage = (_workspaceListPage + 1) % pages; BuildSections(true); }, 144);
    }

    private void BuildWorkspaceDocument(InstanceFileDocument document)
    {
        var header = Stack(_sections, "FileWorkspaceDocumentHeader", XsrUiOrientation.Horizontal, 12);
        var filename = Text(header, document.File.RelativePath + " · " + document.Encoding + " · " + _shell.Renderer.LocalizeText(document.Editable ? "可编辑配置" : "只读"), 13, Ink, 38);
        DesktopLiteralText.Preserve(_shell.Tree, filename); _shell.Tree.GetComponent<XsrUiElement>(filename)!.Weight = 1;
        ManagementButton(header, "返回文件列表", () => { _workspaceDocument = null; _workspacePreview = null; _workspaceError = null; BuildSections(true); }, 128);
        var lines = WorkspaceLines(_workspaceDraft); int pages = Math.Max(1, (lines.Count + 24) / 25); _workspaceTextPage = Math.Clamp(_workspaceTextPage, 0, pages - 1);
        if (lines.Count == 65536) Text(_sections, "此文件行数较多，显示前 65536 行；其余内容将原样保留。", 12, Muted, 28);
        string display = string.Join('\n', lines.Skip(_workspaceTextPage * 25).Take(25).Select((line, index) =>
        {
            string content = _workspaceDraft.Substring(line.Start, Math.Min(line.Length, 120));
            return (_workspaceTextPage * 25 + index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "  " + content + (line.Length > 120 ? "…" : "");
        }));
        var body = Text(_sections, display, 12, Ink, 0); DesktopLiteralText.Preserve(_shell.Tree, body);
        _shell.Tree.GetComponent<XsrUiElement>(body)!.Height = null; _shell.Tree.GetComponent<XsrUiText>(body)!.MaxLines = 0; _shell.Tree.GetComponent<XsrUiText>(body)!.TrimOverflow = false;
        _shell.Tree.GetComponent<XsrUiVisualStyle>(body)!.WrapText = true;
        if (pages > 1) ManagementButton(_sections, $"{_workspaceTextPage + 1}/{pages} · " + _shell.Renderer.LocalizeText("下一段"), () => { CaptureWorkspaceLine(); _workspaceTextPage = (_workspaceTextPage + 1) % pages; BuildSections(true); }, 144);
        if (!document.Editable) return;
        _workspaceLine = Math.Clamp(_workspaceLine, 0, Math.Max(0, lines.Count - 1));
        _workspaceLineNumber = ManagementField(_sections, "FileWorkspaceLineNumber", "编辑行号", (_workspaceLine + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        ManagementButton(_sections, "选择此行", () =>
        {
            if (!int.TryParse(_shell.Tree.GetComponent<XsrUiTextInput>(_workspaceLineNumber)?.ReadDraft(), out int number) || number < 1 || number > lines.Count)
            { _feedback.Error("请输入当前文件中的有效行号。"); return; }
            CaptureWorkspaceLine(); _workspaceLine = number - 1; _workspacePreview = null; BuildSections(true);
        }, 106);
        var selected = lines[_workspaceLine];
        if (selected.Length <= 32768)
        {
            _workspaceLineEditor = ManagementField(_sections, "FileWorkspaceLineEditor", "行内容", _workspaceDraft.Substring(selected.Start, selected.Length));
            _shell.Tree.SetComponent(_workspaceLineEditor, new XsrUiTextInput { MaximumLength = 32768, PreserveTabs = true });
            _shell.Renderer.SetTextInputValue(_workspaceLineEditor, _workspaceDraft.Substring(selected.Start, selected.Length));
        }
        else Text(_sections, "此行过长，可从文件夹中使用外部编辑器。", 12, Muted, 30);
        ManagementButton(_sections, "预览保存", () =>
        {
            CaptureWorkspaceLine(); _workspaceError = null; _workspacePreview = null;
            if (_workspacePreviewRead is not null || !_queries.TryResolve(InstanceFileWorkspaceContract.Preview, out var route)) return;
            _workspaceStop ??= new(); _workspacePreviewRead = _queries.QueryAsync<InstanceFileSavePreviewQuery, InstanceFileSavePreview>(route,
                new(document, _workspaceDraft), cancellationToken: _workspaceStop.Token).AsTask(); WakeOnPlatformCompletion(_workspacePreviewRead);
        }, 106);
        if (_workspacePreview is not { } preview) return;
        Text(_sections, string.Format(System.Globalization.CultureInfo.CurrentCulture, _shell.Renderer.LocalizeText("保存预览：{0} → {1} bytes，{2} → {3} 行。原件将保留为 .nexa-backup。"),
            preview.PreviousBytes, preview.UpdatedBytes, preview.PreviousLines, preview.UpdatedLines), 12, Muted, 34);
        ManagementButton(_sections, "确认保存配置", () =>
        {
            CaptureWorkspaceLine();
            if (_workspaceDraft != preview.Text) { _workspacePreview = null; _feedback.Error("内容在预览后变化，请重新预览。"); return; }
            DispatchWorld(InstanceFileWorkspaceContract.Save, new InstanceFileSaveCommand(preview));
        }, 128);
    }

    private void CaptureWorkspaceLine()
    {
        if (!_workspaceLineEditor.IsAssigned || !_shell.Tree.IsAlive(_workspaceLineEditor) || _shell.Tree.GetComponent<XsrUiTextInput>(_workspaceLineEditor) is not { } editor) return;
        var lines = WorkspaceLines(_workspaceDraft); if (_workspaceLine >= lines.Count) return;
        var selected = lines[_workspaceLine]; string text = editor.ReadDraft();
        if (selected.Length == text.Length && _workspaceDraft.AsSpan(selected.Start, selected.Length).SequenceEqual(text)) return;
        _workspaceDraft = _workspaceDraft[..selected.Start] + text + _workspaceDraft[(selected.Start + selected.Length)..];
    }
    private static List<(int Start, int Length)> WorkspaceLines(string text)
    {
        List<(int, int)> lines = []; int start = 0;
        for (int index = 0; index < text.Length; index++)
            if (text[index] is '\r' or '\n')
            {
                lines.Add((start, index - start)); if (lines.Count == 65536) return lines;
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                start = index + 1;
            }
        lines.Add((start, text.Length - start)); return lines;
    }
}
