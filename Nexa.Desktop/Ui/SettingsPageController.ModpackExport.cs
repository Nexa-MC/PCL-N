using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private InstanceModpackExportPreview? _exportPreview;
    private Task<XsrResult<InstanceModpackExportPreview>>? _exportRead;
    private CancellationTokenSource? _exportStop;
    private string? _exportInstance, _exportError;
    private bool _exportLoaded;
    private readonly HashSet<string> _exportCategories = new(StringComparer.Ordinal) { "mods", "config", "defaultconfigs" };
    private XsrUiEntityId _exportName, _exportVersion, _exportPath;
    private string _exportNameDraft = "", _exportVersionDraft = "1.0.0", _exportPathDraft = "";
    private Func<Task<string?>>? _pickExportDirectory;
    private Task<string?>? _exportDirectoryRead;
    internal void ConfigureExport(Func<Task<string?>> picker) => _pickExportDirectory = picker;
    private void CancelExport()
    {
        _exportStop?.Cancel(); _exportStop?.Dispose(); _exportStop = null; _exportRead = null; _exportPreview = null;
        _exportInstance = null; _exportError = null; _exportLoaded = false; _exportDirectoryRead = null;
        _exportNameDraft = ""; _exportVersionDraft = "1.0.0"; _exportPathDraft = "";
        _exportCategories.Clear(); _exportCategories.UnionWith(["mods", "config", "defaultconfigs"]);
    }
    private void UpdateExport()
    {
        if (_selected != "modpack") { if (_exportInstance is not null) CancelExport(); return; }
        if (_exportInstance != _instance) { CancelExport(); _exportInstance = _instance; }
        if (_selected != "modpack" || _instance is null || !_managementLoaded) return;
        CaptureExportDrafts();
        if (_exportDirectoryRead is { IsCompleted: true } picking)
        {
            _exportDirectoryRead = null;
            if (picking.IsCompletedSuccessfully && picking.Result is { } directory)
            {
                _exportPathDraft = Path.Combine(directory, Path.GetFileName(_instance) + ".mrpack");
                if (_shell.Tree.IsAlive(_exportPath)) _shell.Renderer.SetTextInputValue(_exportPath, _exportPathDraft);
            }
            else if (picking.IsFaulted) _feedback.Error("无法选择导出目录。");
        }
        if (!_exportLoaded && _exportRead is null && _queries.TryResolve(InstanceModpackExportContract.Preview, out var route))
        {
            _exportStop = new(); _exportRead = _queries.QueryAsync<InstanceModpackExportQuery, InstanceModpackExportPreview>(route, new(_instance), cancellationToken: _exportStop.Token).AsTask();
            WakeOnPlatformCompletion(_exportRead);
        }
        if (_exportRead is not { IsCompleted: true } read) return;
        _exportRead = null; _exportLoaded = true;
        if (PendingQuery.Succeeded(read)) _exportPreview = read.Result.Value;
        else _exportError = "无法读取导出范围，请检查目录后刷新。";
        BuildSections();
    }
    private void CaptureExportDrafts()
    {
        string Draft(XsrUiEntityId field, string fallback) => field.IsAssigned && _shell.Tree.IsAlive(field)
            ? _shell.Tree.GetComponent<XsrUiTextInput>(field)!.ReadDraft() : fallback;
        _exportNameDraft = Draft(_exportName, _exportNameDraft); _exportVersionDraft = Draft(_exportVersion, _exportVersionDraft); _exportPathDraft = Draft(_exportPath, _exportPathDraft);
    }
    private void BuildModpackExport(InstanceManagementSnapshot snapshot)
    {
        if (_exportNameDraft.Length == 0) _exportNameDraft = Path.GetFileName(snapshot.InstanceDirectory);
        var metadata = FormGroup(_sections, "ExportMetadata", "整合包信息");
        ManagementFactIn(metadata, "Minecraft", snapshot.GameVersion);
        ManagementFactIn(metadata, "已记录版本", snapshot.ModpackVersion.Length > 0 ? snapshot.ModpackVersion : "未记录");
        _exportName = ManagementField(metadata, "ExportName", "包名称", _exportNameDraft);
        _exportVersion = ManagementField(metadata, "ExportVersion", "包版本", _exportVersionDraft);
        _exportPath = ManagementField(metadata, "ExportPath", "保存到", _exportPathDraft, "完整 .mrpack 文件路径");
        if (_pickExportDirectory is not null) ManagementButton(metadata, "选择保存目录", () =>
        {
            if (_exportDirectoryRead is not null) return;
            _exportDirectoryRead = _pickExportDirectory(); WakeOnPlatformCompletion(_exportDirectoryRead);
        }, 128);
        if (_exportPreview is { } preview)
        {
            var categories = FormGroup(_sections, "ExportCategories", "包含的内容");
            foreach (var category in preview.Categories)
            {
                bool selected = _exportCategories.Contains(category.Id);
                var row = Stack(categories, "ExportCategory." + category.Id, XsrUiOrientation.Horizontal, 12);
                var choice = ToggleControl(row, "ExportCategoryChoice." + category.Id, category.Label, selected,
                    ManagementAction, checkBox: true);
                _shell.Tree.GetComponent<XsrUiElement>(choice)!.Weight = 1;
                _shell.Tree.GetComponent<XsrUiElement>(choice)!.Width = null;
                Text(row, category.FileCount + " 个文件", 12, Muted, 36);
                Text(row, (category.Bytes / 1048576d).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " MiB", 12, Muted, 36);
                _managementActions[choice] = () =>
                {
                    CaptureExportDrafts();
                    if (!_exportCategories.Remove(category.Id)) _exportCategories.Add(category.Id);
                    _shell.Tree.GetComponent<XsrUiToggle>(choice)!.IsChecked = _exportCategories.Contains(category.Id);
                    _shell.Tree.MarkDirty(choice, XsrUiDirtyKinds.Paint);
                };
            }
            Text(_sections, "配置和模组默认包含。存档与游戏选项需自行选择；分享前请检查配置中的敏感信息和文件的分发许可。", 12, Muted, 32);
            ManagementButton(_sections, "导出整合包", () =>
            {
                if (_managementWrite is not null || _instance is null || !_commands.TryResolve(InstanceModpackExportContract.Export, out var route)) return;
                _managementWriteInstance = _instance;
                _managementWrite = _commands.Dispatch(route, new InstanceModpackExportCommand(_instance,
                    _shell.Tree.GetComponent<XsrUiTextInput>(_exportPath)!.ReadDraft(),
                    _shell.Tree.GetComponent<XsrUiTextInput>(_exportName)!.ReadDraft(),
                    _shell.Tree.GetComponent<XsrUiTextInput>(_exportVersion)!.ReadDraft(), preview.Categories.Where(c => _exportCategories.Contains(c.Id)).Select(c => c.Id).ToArray())).Completion;
                WakeOnPlatformCompletion(_managementWrite);
            }, 120);
        }
        else Text(_sections, _exportError ?? "正在统计可导出的文件…", 13, Muted, 28);
    }
}
