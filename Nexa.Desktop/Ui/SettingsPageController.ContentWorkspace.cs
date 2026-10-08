using System.Collections.Concurrent;
using Nexa.Services.Logging;
using Nexa.Services.Setup;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId ContentWorkspaceAction = XsrSemanticId.Parse("ui.settings.storage.workspace");
    private readonly Dictionary<XsrUiEntityId, (string Kind, string? Identity)> _contentWorkspaceActions = [];
    private readonly ConcurrentQueue<(long Generation, bool Accepted, Func<CancellationToken, Task<ContentWorkspaceResult>> Action)> _contentWorkspaceDecisions = new();
    private DesktopContentWorkspaceRoutes? _contentBackups, _legacyMigration, _diagnosticHistory;
    private Func<CancellationToken, Task<string?>>? _contentChooseDirectory, _contentChooseFile;
    private Func<string>? _diagnosticRawCapture;
    private IReadOnlyList<ContentBackupManifest> _contentBackupRows = [];
    private ContentStoreStatistics? _contentStoreStatistics;
    private ContentBackupRetentionPolicy _contentRetention = new();
    private IReadOnlyList<DurableDiagnosticEntry> _durableDiagnosticRows = [];
    private Task<ContentWorkspaceResult>? _contentWorkspaceTask;
    private CancellationTokenSource? _contentWorkspaceStop;
    private Guid _contentWorkspaceDialog;
    private long _contentWorkspaceGeneration;
    private long _contentAiPolicyRevision = -1;
    private bool _contentWorkspaceLoaded;
    private string? _contentWorkspaceMessage;
    private sealed record DiagnosticRawConfirmation(DiagnosticRawPreview Preview, string Destination);
    private sealed record ContentWorkspaceResult(string? Message = null, ContentStoreStatistics? Statistics = null,
        IReadOnlyList<ContentBackupManifest>? Backups = null, IReadOnlyList<DurableDiagnosticEntry>? History = null,
        string? Confirmation = null, Func<CancellationToken, Task<ContentWorkspaceResult>>? Apply = null, ContentBackupRetentionPolicy? Retention = null,
        DiagnosticAiConfirmation? AiConfirmation = null, DiagnosticAiSuggestion? AiSuggestion = null, DiagnosticRawConfirmation? RawConfirmation = null);
    private bool ContentWorkspaceBusy => _contentWorkspaceTask is not null || _contentWorkspaceDialog != Guid.Empty;

    internal void ConfigureContentWorkspace(Func<CancellationToken, Task<string?>> chooseDirectory,
        Func<CancellationToken, Task<string?>> chooseFile, Func<string> captureRaw)
    {
        _contentBackups = _legacyMigration = _diagnosticHistory = new(_queries, _commands);
        _contentChooseDirectory = chooseDirectory; _contentChooseFile = chooseFile; _diagnosticRawCapture = captureRaw;
    }

    private void BuildContentWorkspace()
    {
        _contentWorkspaceActions.Clear();
        if (_instanceDirectory is not null || _selected != "storage" || _contentBackups is null) return;
        var panel = FormGroup(_sections, "ContentWorkspace", "内容存储与独立备份");
        if (_contentStoreStatistics is { } stats)
        {
            Text(panel, $"{stats.Backups} 个备份 · {stats.Objects} 个对象 · 实际 {stats.PhysicalBytes} 字节 · 逻辑 {stats.LogicalBytes} 字节 · 可回收 {stats.ReclaimableBytes} 字节", 11, Muted, 48);
            Text(panel, string.Format(System.Globalization.CultureInfo.CurrentCulture, _shell.Renderer.LocalizeText("备份内容去重节省：{0} 字节；不含清单及文件系统开销。"), Math.Max(0, stats.LogicalBytes - stats.ReferencedBytes)), 11, Muted, 32);
            Text(panel, stats.ReclaimableBytes > 0 ? "存在未引用的备份对象，可预览清理后确认回收。" : "没有未引用的备份对象，无需回收。", 11, Muted, 32);
        }
        else Text(panel, "捕获世界、配置或截图文件夹；对象按内容去重，不链接到可写游戏文件。", 11, Muted, 34);
        var toolbar = Stack(panel, "ContentWorkspace.Toolbar", XsrUiOrientation.Horizontal, 8);
        WorkspaceButton(toolbar, "Refresh", "刷新", "refresh"); WorkspaceButton(toolbar, "Capture", "创建备份", "capture");
        WorkspaceButton(toolbar, "Import", "导入薄备份", "import"); WorkspaceButton(toolbar, "Prune", "预览清理", "prune");
        WorkspaceButton(panel, "Retention", _contentRetention.Enabled ? "停用自动清理" : "启用自动清理", "retention");
        foreach (var manifest in _contentBackupRows.Take(16))
        {
            var row = Stack(panel, "ContentWorkspace.Backup." + manifest.Identity, XsrUiOrientation.Vertical, 6);
            var caption = Text(row, manifest.Label + $" · {manifest.Files.Count} " + _shell.Renderer.LocalizeText("个文件")
                + $" · {manifest.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}", 12, Ink, 28);
            DesktopLiteralText.Preserve(_shell.Tree, caption);
            var actions = Stack(row, "ContentWorkspace.Actions." + manifest.Identity, XsrUiOrientation.Horizontal, 8);
            WorkspaceButton(actions, "Verify." + manifest.Identity, "离线校验", "verify", manifest.Identity);
            WorkspaceButton(actions, "Restore." + manifest.Identity, "恢复副本", "restore", manifest.Identity);
            WorkspaceButton(actions, "Export." + manifest.Identity, "导出薄备份", "export", manifest.Identity);
        }
        if (_contentBackupRows.Count > 16) Text(panel, "显示最近 16 个备份，清理后刷新查看较早项目。", 11, Muted, 28);
        var migration = FormGroup(_sections, "LegacyMigrationWorkspace", "迁移助手");
        Text(migration, "预览旧启动器 JSON 设置和离线账户；在线账户需要重新登录。来源保留，新目录不覆盖现有数据。", 11, Muted, 40);
        WorkspaceButton(migration, "Migrate", "预览旧数据迁移", "migrate");
        var diagnostics = FormGroup(_sections, "DurableDiagnosticWorkspace", "持久诊断历史");
        Text(diagnostics, "历史仅保留操作、阶段和结果；原始文本导出先脱敏再确认。", 11, Muted, 32);
        WorkspaceButton(diagnostics, "Raw", "预览原始诊断导出", "raw");
        foreach (var entry in _durableDiagnosticRows.Take(12))
            Text(diagnostics, $"{entry.Timestamp.ToLocalTime():MM-dd HH:mm} · {entry.Module} / {entry.Operation} / {entry.Stage} · {entry.Outcome}", 11, Muted, 26);
        BuildDiagnosticAiWorkspace();
        if (_contentWorkspaceMessage is { } message)
        { var text = Text(panel, message, 11, Muted, 60); _shell.Tree.GetComponent<XsrUiVisualStyle>(text)!.WrapText = true; DesktopLiteralText.Preserve(_shell.Tree, text); }
    }

    private void WorkspaceButton(XsrUiEntityId parent, string name, string label, string kind, string? identity = null)
    {
        var button = ActionButton(parent, "ContentWorkspace." + name, label, ContentWorkspaceAction, 116);
        _shell.Tree.GetComponent<XsrUiInput>(button)!.Enabled = !ContentWorkspaceBusy;
        _contentWorkspaceActions[button] = (kind, identity);
    }

    private void HandleContentWorkspace(DesktopUiIntent intent)
    {
        if (!_visible || _instanceDirectory is not null || _selected != "storage" || ContentWorkspaceBusy || _contentBackups is null
            || !_shell.Tree.IsAlive(intent.Source) || _shell.Tree.GetComponent<XsrUiInput>(intent.Source)?.Enabled != true
            || !_contentWorkspaceActions.TryGetValue(intent.Source, out var action)) return;
        CaptureDiagnosticAiDraft();
        var aiDraft = action.Kind is "ai-facts" or "ai-raw" ? ReadDiagnosticAiRequest(action.Kind) : null;
        if (action.Kind is "ai-facts" or "ai-raw" && aiDraft is null) return;
        CancelContentWorkspace(); _contentWorkspaceStop = new();
        if (aiDraft is not null) _contentAiPolicyRevision = _store.Read<long>(_revisionId).Value;
        // Raw capture reads the current local log snapshot only on this explicit UI action.
        string? raw = action.Kind is "raw" or "ai-raw" ? _diagnosticRawCapture?.Invoke() : null;
        _contentWorkspaceTask = aiDraft is not null ? PreviewDiagnosticAiAsync(aiDraft, raw, _contentWorkspaceStop.Token)
            : RunContentWorkspaceAsync(action.Kind, action.Identity, raw, _contentWorkspaceStop.Token);
        ObserveTransfer(_contentWorkspaceTask); BuildSections(); UpdateEditors();
    }

    private async Task<ContentWorkspaceResult> RefreshContentWorkspaceAsync(string? message, CancellationToken token) => new(message,
        await _contentBackups!.StatisticsAsync(token).ConfigureAwait(false), await _contentBackups.ListAsync(token).ConfigureAwait(false),
        await _diagnosticHistory!.ReadAsync(token).ConfigureAwait(false), Retention: await _contentBackups.ReadRetentionAsync(token).ConfigureAwait(false));

    private async Task<ContentWorkspaceResult> RunContentWorkspaceAsync(string kind, string? identity, string? raw, CancellationToken token)
    {
        if (kind == "refresh") return await RefreshContentWorkspaceAsync(null, token).ConfigureAwait(false);
        if (kind == "retention")
        {
            var policy = await _contentBackups!.ReadRetentionAsync(token).ConfigureAwait(false);
            var changed = policy with { Enabled = !policy.Enabled };
            return new(Confirmation: changed.Enabled ? $"启用空闲时自动清理：保留最多 {policy.KeepCount} 个、{policy.KeepDays} 天的备份，始终保留最新备份；仅删除未引用对象。" : "停用自动备份清理，现有备份保持原状。",
                Apply: async cancellation => { await _contentBackups.SetRetentionAsync(changed, cancellation).ConfigureAwait(false); return await RefreshContentWorkspaceAsync("备份保留策略已保存。", cancellation).ConfigureAwait(false); });
        }
        if (kind == "verify")
        {
            var report = await _contentBackups!.VerifyOfflineAsync(identity!, token).ConfigureAwait(false);
            return new(report.Ready ? $"离线就绪：{report.VerifiedFiles} 个文件均已校验。" : $"尚未离线就绪：{report.MissingOrCorruptPaths.Count} 个文件缺失或损坏。\n" + string.Join('\n', report.MissingOrCorruptPaths.Take(8)));
        }
        if (kind == "prune")
        {
            var policy = await _contentBackups!.ReadRetentionAsync(token).ConfigureAwait(false);
            var preview = await _contentBackups.PreviewPruneAsync(policy.KeepCount, policy.KeepDays, token).ConfigureAwait(false);
            return new(Confirmation: $"保留最新备份；清理超过 {policy.KeepCount} 个或 {policy.KeepDays} 天的旧备份。\n将删除 {preview.BackupIdentities.Count} 个清单，回收 {preview.Objects} 个未引用对象（{preview.Bytes} 字节）。",
                Apply: async cancellation => { await _contentBackups.ApplyPruneAsync(policy.KeepCount, policy.KeepDays, preview.Revision, cancellation).ConfigureAwait(false); return await RefreshContentWorkspaceAsync("备份清理完成。", cancellation).ConfigureAwait(false); });
        }
        if (kind == "import")
        {
            string? file = await _contentChooseFile!(token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); if (file is null) return new();
            return new(Confirmation: "导入所选薄备份清单，不下载对象；完成后报告本地对象可用情况。",
                Apply: async cancellation => { await _contentBackups!.ImportThinAsync(file, cancellation).ConfigureAwait(false); return await RefreshContentWorkspaceAsync("薄备份已导入；请使用离线校验检查本地对象。", cancellation).ConfigureAwait(false); });
        }
        if (kind == "migrate")
        {
            string? source = await _contentChooseDirectory!(token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); if (source is null) return new();
            string? parent = await _contentChooseDirectory(token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); if (parent is null) return new();
            var preview = await _legacyMigration!.PreviewAsync(source, Path.Combine(parent, "NexaCL-import"), token).ConfigureAwait(false);
            return new(Confirmation: $"迁移 {preview.Settings} 个设置、{preview.OfflineAccounts} 个离线账户；{preview.ReauthenticationAccounts} 个在线账户需重新登录。\n目标：{preview.DestinationDirectory}\n" + string.Join('\n', preview.Warnings),
                Apply: async cancellation => { await _legacyMigration.ApplyAsync(preview, cancellation).ConfigureAwait(false); return new("迁移副本已创建。可在设置的数据位置中选择该目录；源数据保持原状。"); });
        }
        string? directory = await _contentChooseDirectory!(token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); if (directory is null) return new();
        if (kind == "capture")
            return new(Confirmation: "将复制并校验所选文件夹，保存为独立备份。请确保游戏已退出。\n" + directory,
                Apply: async cancellation => { await _contentBackups!.CaptureAsync(directory, Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)) is { Length: > 0 } name ? name : "备份", cancellation).ConfigureAwait(false); return await RefreshContentWorkspaceAsync("独立备份已创建。", cancellation).ConfigureAwait(false); });
        if (kind == "restore")
        {
            string destination = Path.Combine(directory, "restore-" + identity);
            return new(Confirmation: "恢复到新的副本目录，现有文件不会被覆盖：\n" + destination,
                Apply: async cancellation => { await _contentBackups!.RestoreAsync(identity!, destination, cancellation).ConfigureAwait(false); return new("备份副本已校验并恢复：" + destination); });
        }
        if (kind == "export")
        {
            string destination = Path.Combine(directory, "nexa-thin-" + identity + ".json");
            return new(Confirmation: "薄备份包含文件路径和 SHA-256 身份，不包含文件正文。\n" + destination,
                Apply: async cancellation => { await _contentBackups!.ExportThinAsync(identity!, destination, cancellation).ConfigureAwait(false); return new("薄备份已导出。"); });
        }
        if (kind == "raw")
        {
            var preview = await _contentBackups!.PreviewRawAsync(raw ?? "", token).ConfigureAwait(false); string destination = Path.Combine(directory, "nexa-raw-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".txt");
            return new(RawConfirmation: new(preview, destination),
                Apply: async cancellation => { await _contentBackups.ExportRawAsync(destination, preview, preview.Revision, cancellation).ConfigureAwait(false); return new("脱敏诊断工作区已导出。"); });
        }
        return new();
    }

    private void UpdateContentWorkspace()
    {
        CaptureDiagnosticAiDraft();
        bool visible = _visible && _instanceDirectory is null && _selected == "storage";
        if (!visible) { if (ContentWorkspaceBusy) CancelContentWorkspace(); _diagnosticAiDraft["key"] = ""; return; }
        if (_contentAiPolicyRevision >= 0 && (!DiagnosticAiEnabled || _store.Read<long>(_revisionId).Value != _contentAiPolicyRevision))
        { CancelContentWorkspace(); _diagnosticAiDraft["key"] = ""; _feedback.Info("AI 设置已改变，请重新预览。"); }
        // The current controls must consume their queued intent before refresh completion replaces them.
        if (_pending.Any(intent => intent.Command == Choice || intent.Command == ContentWorkspaceAction)) return;
        if (_diagnosticAiWorkspaceEnabled is { } builtEnabled && builtEnabled != DiagnosticAiEnabled)
        {
            if (!DiagnosticAiEnabled) _diagnosticAiDraft["key"] = "";
            BuildSections(); UpdateEditors();
        }
        if (_contentBackups is null) return;
        if (!_contentWorkspaceLoaded && _contentWorkspaceTask is null)
        {
            _contentWorkspaceLoaded = true; _contentWorkspaceStop = new();
            _contentWorkspaceTask = RefreshContentWorkspaceAsync(null, _contentWorkspaceStop.Token); ObserveTransfer(_contentWorkspaceTask);
        }
        if (_contentWorkspaceTask is { IsCompleted: true } completed)
        {
            _contentWorkspaceTask = null;
            if (completed.IsFaulted) _feedback.Error(completed.Exception!.GetBaseException().Message);
            else if (completed.IsCompletedSuccessfully)
            {
                var result = completed.Result;
                if (result.Apply is null) _contentAiPolicyRevision = -1;
                if (result.Statistics is not null) _contentStoreStatistics = result.Statistics;
                if (result.Backups is not null) _contentBackupRows = result.Backups;
                if (result.History is not null) _durableDiagnosticRows = result.History;
                if (result.Retention is not null) _contentRetention = result.Retention;
                if (result.Message is not null) { _contentWorkspaceMessage = result.Message; _feedback.Info(result.Message); }
                if (result.AiSuggestion is { } suggestion) PresentDiagnosticAiSuggestion(suggestion);
                string? confirmation = result.AiConfirmation is { } ai ? FormatDiagnosticAiConfirmation(ai)
                    : result.RawConfirmation is { } raw ? FormatDiagnosticRawConfirmation(raw) : result.Confirmation;
                if (result.Apply is { } apply && confirmation is not null)
                {
                    long generation = _contentWorkspaceGeneration;
                    _contentWorkspaceDialog = _feedback.ShowDialog("settings.content-workspace", "确认存储操作", confirmation, "确认", "返回",
                        accepted => { if (Volatile.Read(ref _contentWorkspaceGeneration) == generation) _contentWorkspaceDecisions.Enqueue((generation, accepted, apply)); },
                        localizeMessage: result.AiConfirmation is null && result.RawConfirmation is null);
                }
            }
            BuildSections(); UpdateEditors();
        }
        while (_contentWorkspaceDecisions.TryDequeue(out var decision))
        {
            if (decision.Generation != _contentWorkspaceGeneration || _contentWorkspaceStop?.IsCancellationRequested != false) continue;
            _contentWorkspaceDialog = default;
            if (decision.Accepted) { _contentWorkspaceTask = decision.Action(_contentWorkspaceStop.Token); ObserveTransfer(_contentWorkspaceTask); }
            BuildSections(); UpdateEditors();
        }
    }

    private string FormatDiagnosticRawConfirmation(DiagnosticRawConfirmation confirmation)
    {
        string format = _shell.Renderer.LocalizeText("导出脱敏文本 {0} 字节；内容可能包含您自行输入的未标记个人信息，请检查预览。\n{1}\n");
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, format, confirmation.Preview.Utf8Bytes,
            confirmation.Destination) + confirmation.Preview.RedactedText;
    }

    private void CancelContentWorkspace()
    {
        Interlocked.Increment(ref _contentWorkspaceGeneration); _contentWorkspaceStop?.Cancel(); _contentWorkspaceStop?.Dispose(); _contentWorkspaceStop = null;
        _contentAiPolicyRevision = -1;
        _contentWorkspaceTask = null; _contentWorkspaceActions.Clear();
        if (_contentWorkspaceDialog != Guid.Empty) _feedback.DismissDialog(_contentWorkspaceDialog); _contentWorkspaceDialog = default;
        if (_diagnosticAiResultDialog != Guid.Empty) _feedback.DismissDialog(_diagnosticAiResultDialog); _diagnosticAiResultDialog = default;
        while (_contentWorkspaceDecisions.TryDequeue(out _)) { }
    }
}
