using System.Collections.Concurrent;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private const int SettingsDocumentBudget = 1024 * 1024;
    private static readonly XsrSemanticId ImportSettings = XsrSemanticId.Parse("ui.settings.data.import");
    private static readonly XsrSemanticId ExportSettings = XsrSemanticId.Parse("ui.settings.data.export");
    private static readonly XsrSemanticId ResetSettings = XsrSemanticId.Parse("ui.settings.data.reset");
    private static bool IsSettingsTransferIntent(XsrSemanticId command) => command == ImportSettings || command == ExportSettings || command == ResetSettings;
    private sealed record ImportResult(string Document, SettingsImportPreview Preview, bool Reset = false);
    private Func<CancellationToken, Task<string?>>? _readSettingsDocument;
    private Func<string, CancellationToken, Task<bool>>? _saveSettingsDocument;
    private CancellationTokenSource? _settingsTransferStop;
    private Task<ImportResult?>? _settingsImportRead;
    private Task<bool>? _settingsExportWrite;
    private Task<XsrResult>? _settingsImportApply;
    private bool _settingsResetApplying;
    private Guid _settingsImportDialog;
    private long _settingsTransferGeneration;
    private readonly ConcurrentQueue<(long Generation, bool Accepted, ImportResult Result)> _settingsImportDecisions = new();
    private bool SettingsTransferBusy => _settingsImportRead is not null || _settingsExportWrite is not null
        || _settingsImportApply is not null || _settingsImportDialog != Guid.Empty;

    internal void ConfigureSettingsTransfer(Func<CancellationToken, Task<string?>> read,
        Func<string, CancellationToken, Task<bool>> save)
    { _readSettingsDocument = read; _saveSettingsDocument = save; }

    private void BuildSettingsTransfer()
    {
        var group = Stack(_sections, "SettingsTransfer", XsrUiOrientation.Vertical, 10);
        Text(group, "设置数据", 18, Ink, height: 28, weight: 600);
        var divider = Element(group, "SettingsGroupDivider", XsrUiSemanticRole.None, null, height: 1); Style(divider, Line, Muted, 0);
        Row("SettingsImport", "导入设置", "先预览更改，确认后应用。", "选择文件", ImportSettings, _readSettingsDocument is not null);
        Row("SettingsExport", "导出设置", "不包含账户、凭据或本机路径。", "导出", ExportSettings, _saveSettingsDocument is not null);
        Row("SettingsReset", _instance is null ? "恢复默认" : "恢复继承", "仅重置已接入的设置，不删除版本或文件。", "恢复", ResetSettings,
            _queries.TryResolve(SettingsPolicyContract.ResetPreviewQuery, out _) && _commands.TryResolve(SettingsPolicyContract.ResetCommand, out _));
        void Row(string name, string label, string hint, string action, XsrSemanticId command, bool configured)
        {
            var row = Stack(group, name + ".Row", XsrUiOrientation.Horizontal, 16);
            _shell.Tree.GetComponent<XsrUiElement>(row)!.Padding = new(0, 6, 0, 6);
            var labels = Stack(row, name + ".Label", XsrUiOrientation.Vertical, 3);
            _shell.Tree.GetComponent<XsrUiElement>(labels)!.Weight = 1;
            Text(labels, label, 14, Ink, height: 22, weight: 500);
            Text(labels, hint, 11, Muted, height: 22);
            var button = ActionButton(row, name, action, command, 84);
            _shell.Tree.GetComponent<XsrUiInput>(button)!.Enabled = configured && !SettingsTransferBusy;
        }
    }

    private void HandleSettingsTransfer(XsrSemanticId command, XsrUiEntityId source)
    {
        if (!(_instanceDirectory is null && _selected == "storage" || _instanceDirectory is not null && _selected == "game")) return;
        if (!_shell.Tree.IsAlive(source) || _shell.Tree.Name(source) != (command == ImportSettings ? "SettingsImport" : command == ResetSettings ? "SettingsReset" : "SettingsExport")) return;
        if (SettingsTransferBusy) return;
        if (command == ImportSettings && _readSettingsDocument is null || command == ExportSettings && _saveSettingsDocument is null) return;
        CancelSettingsTransfer();
        _settingsTransferStop = new(); CancellationToken token = _settingsTransferStop.Token;
        string? instance = _instance;
        if (command == ImportSettings) { _settingsImportRead = ReadImportAsync(instance, token); ObserveTransfer(_settingsImportRead); }
        else if (command == ResetSettings) { _settingsImportRead = ReadResetAsync(instance, token); ObserveTransfer(_settingsImportRead); }
        else { _settingsExportWrite = ExportAsync(instance, token); ObserveTransfer(_settingsExportWrite); }
        BuildSections();
    }

    private async Task<ImportResult?> ReadResetAsync(string? instance, CancellationToken token)
    {
        if (!_queries.TryResolve(SettingsPolicyContract.ResetPreviewQuery, out var route)) throw new InvalidOperationException("Reset is unavailable.");
        var result = await Task.Run(async () => await _queries.QueryAsync<SettingsResetQuery, SettingsResetPreview>(route,
            new(instance), cancellationToken: token).ConfigureAwait(false), token).ConfigureAwait(false);
        if (!result.IsSuccess) throw new InvalidDataException("Reset preview failed.");
        var preview = result.Value!;
        return new("", new(preview.Revision, preview.Changes, preview.Errors), Reset: true);
    }

    private async Task<ImportResult?> ReadImportAsync(string? instance, CancellationToken token)
    {
        string? document = await _readSettingsDocument!(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (document is null) return null;
        if (document.Length > SettingsDocumentBudget) throw new InvalidDataException("Document exceeds its budget.");
        if (!_queries.TryResolve(SettingsPolicyContract.ImportPreviewQuery, out var route)) throw new InvalidOperationException("Import is unavailable.");
        var result = await Task.Run(async () => await _queries.QueryAsync<SettingsImportQuery, SettingsImportPreview>(route,
            new(document, instance), cancellationToken: token).ConfigureAwait(false), token).ConfigureAwait(false);
        if (!result.IsSuccess) throw new InvalidDataException("Import preview failed.");
        return new(document, result.Value!);
    }

    private async Task<bool> ExportAsync(string? instance, CancellationToken token)
    {
        if (!_queries.TryResolve(SettingsPolicyContract.ExportQuery, out var route)) throw new InvalidOperationException("Export is unavailable.");
        var result = await Task.Run(async () => await _queries.QueryAsync<SettingsExportQuery, string>(route,
            new(instance), cancellationToken: token).ConfigureAwait(false), token).ConfigureAwait(false);
        if (!result.IsSuccess) throw new InvalidDataException("Export failed.");
        token.ThrowIfCancellationRequested();
        return await _saveSettingsDocument!(result.Value!, token).ConfigureAwait(false);
    }

    private void ObserveTransfer(Task task)
    {
        _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        WakeOnPlatformCompletion(task);
    }

    private void UpdateSettingsTransfer()
    {
        bool changed = false;
        if (_settingsImportRead is { IsCompleted: true } reading)
        {
            _settingsImportRead = null; changed = true;
            if (reading.IsFaulted) _feedback.Error("无法生成设置预览，请重试。");
            else if (reading.IsCompletedSuccessfully && reading.Result is { } imported)
            {
                if (imported.Preview.Errors.Count > 0) _feedback.Error(imported.Reset ? "无法恢复设置，现有设置未作更改。" : "设置文件的格式、值或作用域不符合要求，未作更改。");
                else if (imported.Preview.Changes.Count == 0) _feedback.Info(imported.Reset ? "没有需要恢复的设置。" : "未发现可导入的更改。");
                else
                {
                    long generation = _settingsTransferGeneration;
                    string labels = string.Join('\n', imported.Preview.Changes.Take(16).Select(change => _shell.Renderer.LocalizeText(DisplayLabel(
                        _catalog!.Entries.FirstOrDefault(entry => entry.SettingKey == change.Key)?.Label ?? "其他设置"))));
                    string message = _shell.Renderer.LocalizeText((imported.Reset ? "将恢复 " : "将导入 ") + imported.Preview.Changes.Count + " 项设置。") + "\n" + labels;
                    if (imported.Preview.Changes.Count > 16) message += "\n" + _shell.Renderer.LocalizeText("另有 " + (imported.Preview.Changes.Count - 16) + " 项更改。");
                    if (imported.Preview.Changes.Any(change => SettingsPolicySchema.ByKey[change.Key].Timing == SettingsApplyTiming.Restart))
                        message += "\n" + _shell.Renderer.LocalizeText("部分设置需要重启后生效。");
                    _settingsImportDialog = _feedback.ShowDialog(imported.Reset ? "settings.reset" : "settings.import",
                        imported.Reset ? (_instance is null ? "恢复默认" : "恢复继承") : "导入设置", message, imported.Reset ? "恢复" : "应用更改", "取消",
                        accepted =>
                        {
                            if (Volatile.Read(ref _settingsTransferGeneration) == generation)
                                _settingsImportDecisions.Enqueue((generation, accepted, imported));
                        });
                }
            }
        }
        while (_settingsImportDecisions.TryDequeue(out var decision))
        {
            if (decision.Generation != _settingsTransferGeneration || _settingsTransferStop?.IsCancellationRequested != false) continue;
            _settingsImportDialog = default; changed = true;
            if (!decision.Accepted) continue;
            _settingsResetApplying = decision.Result.Reset;
            if (!_commands.TryResolve(_settingsResetApplying ? SettingsPolicyContract.ResetCommand : SettingsPolicyContract.ImportCommand, out var route))
            { _feedback.Error("暂时无法应用设置。"); continue; }
            _settingsImportApply = _settingsResetApplying
                ? _commands.Dispatch(route, new SettingsResetCommand(decision.Result.Preview.Revision, _instance), cancellationToken: _settingsTransferStop.Token).Completion
                : _commands.Dispatch(route, new SettingsImportCommand(decision.Result.Document,
                    decision.Result.Preview.Revision, _instance), cancellationToken: _settingsTransferStop.Token).Completion;
            ObserveTransfer(_settingsImportApply);
        }
        if (_settingsImportApply is { IsCompleted: true } applying)
        {
            _settingsImportApply = null; changed = true;
            if (PendingQuery.Succeeded(applying)) _feedback.Info(_settingsResetApplying ? "设置已恢复。" : "设置已导入。");
            else if (!applying.IsCanceled) _feedback.Error(_settingsResetApplying ? "设置未恢复；可能已发生更改，请重新预览。" : "设置未导入；可能已发生更改，请重新选择文件并预览。");
        }
        if (_settingsExportWrite is { IsCompleted: true } exporting)
        {
            _settingsExportWrite = null; changed = true;
            if (exporting.IsCompletedSuccessfully && exporting.Result) _feedback.Info("设置已导出。");
            else if (exporting.IsFaulted) _feedback.Error("无法导出设置，原文件未被替换。");
        }
        if (changed) { BuildSections(); UpdateEditors(); }
    }

    private void CancelSettingsTransfer()
    {
        Interlocked.Increment(ref _settingsTransferGeneration);
        _settingsTransferStop?.Cancel(); _settingsTransferStop?.Dispose(); _settingsTransferStop = null;
        _settingsImportRead = null; _settingsExportWrite = null; _settingsImportApply = null;
        if (_settingsImportDialog != Guid.Empty) _feedback.DismissDialog(_settingsImportDialog);
        _settingsImportDialog = default;
        while (_settingsImportDecisions.TryDequeue(out _)) { }
        int pending = _pending.Count;
        for (int i = 0; i < pending && _pending.TryDequeue(out var intent); i++)
            if (!IsSettingsTransferIntent(intent.Command)) _pending.Enqueue(intent);
    }
}
