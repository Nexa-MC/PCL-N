using System.Collections.Concurrent;
using Nexa.Services.Setup;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId MoveStorage = XsrSemanticId.Parse("ui.settings.storage.move");
    private static readonly XsrSemanticId CleanStorageTemporary = XsrSemanticId.Parse("ui.settings.storage.cleanup-temp");
    private static readonly XsrSemanticId CleanStorageTasks = XsrSemanticId.Parse("ui.settings.storage.cleanup-tasks");
    private static readonly XsrSemanticId CancelStorageMigration = XsrSemanticId.Parse("ui.settings.storage.cancel");
    private static bool IsStoragePreferencesIntent(XsrSemanticId command) => command == MoveStorage || command == CleanStorageTemporary || command == CleanStorageTasks || command == CancelStorageMigration;
    private sealed record StoragePreview(StorageMigrationPreview? Migration, StorageCleanupPreview? Cleanup, string? CancelTransaction = null);
    private Func<CancellationToken, Task<string?>>? _chooseStorageDirectory;
    private Action? _closeForStorageMigration;
    private StoragePreferencesStatus? _storageStatus;
    private Task<XsrResult<StoragePreferencesStatus>>? _storageStatusRead;
    private Task<StoragePreview?>? _storagePreviewRead;
    private Task<XsrResult>? _storageApply;
    private bool _storageApplyingMove;
    private bool _storageApplyingCancel;
    private bool _storageStatusUnavailable;
    private bool _storageRebuildPending;
    private CancellationTokenSource? _storageStop;
    private long _storageGeneration;
    private Guid _storageDialog;
    private readonly ConcurrentQueue<(long Generation, bool Accepted, StoragePreview Preview)> _storageDecisions = new();
    private bool StoragePreferencesBusy => _storagePreviewRead is not null || _storageApply is not null || _storageDialog != Guid.Empty;

    internal void ConfigureStoragePreferences(Func<CancellationToken, Task<string?>> chooseDirectory, Action queuedClose)
    { _chooseStorageDirectory = chooseDirectory; _closeForStorageMigration = queuedClose; }

    private void BuildStoragePreferences()
    {
        if (_instanceDirectory is not null || _selected != "storage") return;
        _storageRebuildPending = false;
        var location = FormGroup(_sections, "SettingsStorageLocation", "启动器数据位置");
        var value = Text(location, _storageStatus?.DataDirectory ?? "正在读取数据位置…", 12, Ink, height: 26);
        DesktopLiteralText.Preserve(_shell.Tree, value);
        var hint = Text(location, _storageStatus?.LocationLocked == true ? "此位置由环境变量指定。"
            : _storageStatus?.MigrationPending == true ? "迁移已排队，重新启动后完成。"
            : "迁移启动器设置和本地记录；Minecraft 与 Java 的独立路径保持原位置。", 11, Muted, height: 34);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(hint)!.WrapText = true;
        var move = ActionButton(location, "SettingsStorageMove", "选择迁移位置", MoveStorage, 112);
        _shell.Tree.GetComponent<XsrUiInput>(move)!.Enabled = !StoragePreferencesBusy && _storageStatus is { LocationLocked: false, MigrationPending: false }
            && _chooseStorageDirectory is not null && _closeForStorageMigration is not null
            && _queries.TryResolve(StoragePreferencesContract.MigrationPreview, out _) && _commands.TryResolve(StoragePreferencesContract.Migrate, out _);
        if (_storageStatus?.MigrationPending == true)
        {
            var cancel = ActionButton(location, "SettingsStorageCancel", "取消待迁移", CancelStorageMigration, 112);
            _shell.Tree.GetComponent<XsrUiInput>(cancel)!.Enabled = !StoragePreferencesBusy && _storageStatus.PendingTransaction is not null
                && _commands.TryResolve(StoragePreferencesContract.CancelMigration, out _);
        }
        var cleanup = FormGroup(_sections, "SettingsStorageCleanup", "安全清理");
        Row("SettingsStorageCleanTemporary", "过期临时文件", "仅清理 24 小时前遗留的启动器原子写入文件。", CleanStorageTemporary);
        Row("SettingsStorageCleanTasks", "成功任务记录", "仅移除成功任务卡片，保留失败、取消、暂停及恢复数据。", CleanStorageTasks);
        void Row(string name, string label, string description, XsrSemanticId command)
        {
            var row = Stack(cleanup, name + ".Row", XsrUiOrientation.Horizontal, 12);
            _shell.Tree.GetComponent<XsrUiElement>(row)!.Padding = new(0, 6, 0, 6);
            var labels = Stack(row, name + ".Labels", XsrUiOrientation.Vertical, 3);
            _shell.Tree.GetComponent<XsrUiElement>(labels)!.Weight = 1;
            Text(labels, label, 14, Ink, height: 22, weight: 500);
            var descriptionEntity = Text(labels, description, 11, Muted, height: 32);
            _shell.Tree.GetComponent<XsrUiVisualStyle>(descriptionEntity)!.WrapText = true;
            var button = ActionButton(row, name, "预览清理", command, 84);
            _shell.Tree.GetComponent<XsrUiInput>(button)!.Enabled = !StoragePreferencesBusy
                && _queries.TryResolve(StoragePreferencesContract.CleanupPreview, out _) && _commands.TryResolve(StoragePreferencesContract.Cleanup, out _);
        }
    }

    private void HandleStoragePreferences(XsrSemanticId command, XsrUiEntityId source)
    {
        if (_instanceDirectory is not null || _selected != "storage" || StoragePreferencesBusy || !_shell.Tree.IsAlive(source)) return;
        string name = command == MoveStorage ? "SettingsStorageMove" : command == CancelStorageMigration ? "SettingsStorageCancel"
            : command == CleanStorageTemporary ? "SettingsStorageCleanTemporary" : "SettingsStorageCleanTasks";
        if (_shell.Tree.Name(source) != name || _shell.Tree.GetComponent<XsrUiInput>(source)?.Enabled != true) return;
        CancelStoragePreferences(); _storageStop = new();
        _storagePreviewRead = ReadStoragePreviewAsync(command, _storageStop.Token);
        ObserveTransfer(_storagePreviewRead); BuildSections();
    }

    private async Task<StoragePreview?> ReadStoragePreviewAsync(XsrSemanticId command, CancellationToken token)
    {
        if (command == CancelStorageMigration) return new(null, null, _storageStatus?.PendingTransaction);
        if (command == MoveStorage)
        {
            string? selected = await _chooseStorageDirectory!(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (selected is null) return null;
            if (!_queries.TryResolve(StoragePreferencesContract.MigrationPreview, out var route)) throw new IOException("迁移暂时不可用。");
            var preview = await _queries.QueryAsync<StorageMigrationQuery, StorageMigrationPreview>(route, new(selected), cancellationToken: token).ConfigureAwait(false);
            if (!preview.IsSuccess) throw new IOException(preview.Error?.Message ?? "无法生成迁移预览。");
            return new(preview.Value, null);
        }
        if (!_queries.TryResolve(StoragePreferencesContract.CleanupPreview, out var cleanupRoute)) throw new IOException("清理暂时不可用。");
        StorageCleanupKind kind = command == CleanStorageTemporary ? StorageCleanupKind.TemporaryFiles : StorageCleanupKind.FinishedTasks;
        var cleanup = await _queries.QueryAsync<StorageCleanupQuery, StorageCleanupPreview>(cleanupRoute, new(kind), cancellationToken: token).ConfigureAwait(false);
        if (!cleanup.IsSuccess) throw new IOException(cleanup.Error?.Message ?? "无法生成清理预览。");
        return new(null, cleanup.Value);
    }

    // Run this before OnFrame's visibility return so a committed queue still closes the host after navigation.
    private void UpdateStoragePreferences()
    {
        bool changed = false;
        if (_instanceDirectory is null && _selected == "storage" && _storageStatus is null && _storageStatusRead is null
            && !_storageStatusUnavailable
            && _queries.TryResolve(StoragePreferencesContract.Status, out var statusRoute))
        {
            _storageStatusRead = _queries.QueryAsync<StoragePreferencesQuery, StoragePreferencesStatus>(statusRoute, new()).AsTask();
            ObserveTransfer(_storageStatusRead);
        }
        if (_storageStatusRead is { IsCompleted: true } status)
        {
            _storageStatusRead = null;
            if (PendingQuery.Succeeded(status)) { _storageStatus = status.Result.Value; changed = true; }
            else
            {
                _storageStatusUnavailable = true;
                _feedback.Error(status.IsCompletedSuccessfully ? status.Result.Error?.Message ?? "无法读取存储位置，请重新启动后重试。" : "无法读取存储位置，请重新启动后重试。");
            }
        }
        if (_storagePreviewRead is { IsCompleted: true } reading)
        {
            _storagePreviewRead = null; changed = true;
            if (reading.IsFaulted) _feedback.Error(reading.Exception?.GetBaseException().Message ?? "无法生成存储预览。");
            else if (reading.IsCompletedSuccessfully && reading.Result is { } preview)
            {
                if (preview.Cleanup is { Entries.Count: 0 }) _feedback.Info("没有符合条件的清理项。");
                else
                {
                    long generation = _storageGeneration;
                    string message = preview.CancelTransaction is not null ? "取消待完成迁移，继续使用当前数据位置。已存在的数据不会被删除。" : preview.Migration is { } migration
                        ? "将在下次启动时复制 " + migration.Files + " 个文件（" + migration.Bytes + " 字节）。\n"
                            + migration.SourceDirectory + "\n→ " + migration.DestinationDirectory
                            + "\n确认后启动器关闭；旧数据保留，独立游戏路径保持原位置。"
                        : "将清理 " + preview.Cleanup!.Entries.Count + " 项（" + preview.Cleanup.Entries.Sum(entry => entry.Bytes) + " 字节）。\n"
                            + string.Join('\n', preview.Cleanup.Entries.Take(12).Select(entry => entry.Identity));
                    _storageDialog = _feedback.ShowDialog("settings.storage", preview.CancelTransaction is not null ? "取消待迁移" : preview.Migration is not null ? "迁移启动器数据" : "确认安全清理", message,
                        preview.CancelTransaction is not null ? "取消迁移" : preview.Migration is not null ? "排队并关闭" : "清理", "返回",
                        accepted => { if (Volatile.Read(ref _storageGeneration) == generation) _storageDecisions.Enqueue((generation, accepted, preview)); });
                }
            }
        }
        while (_storageDecisions.TryDequeue(out var decision))
        {
            if (decision.Generation != _storageGeneration || _storageStop?.IsCancellationRequested != false) continue;
            _storageDialog = default; changed = true;
            if (!decision.Accepted) continue;
            _storageApplyingMove = decision.Preview.Migration is not null;
            _storageApplyingCancel = decision.Preview.CancelTransaction is not null;
            if (!_commands.TryResolve(decision.Preview.CancelTransaction is not null ? StoragePreferencesContract.CancelMigration
                : _storageApplyingMove ? StoragePreferencesContract.Migrate : StoragePreferencesContract.Cleanup, out var route))
            { _feedback.Error("暂时无法执行存储操作。"); continue; }
            _storageApply = decision.Preview.CancelTransaction is { } transaction
                ? _commands.Dispatch(route, new StorageMigrationCancelCommand(transaction), cancellationToken: _storageStop.Token).Completion
                : decision.Preview.Migration is { } migration
                ? _commands.Dispatch(route, new StorageMigrationCommand(migration.DestinationDirectory, migration.Revision), cancellationToken: _storageStop.Token).Completion
                : _commands.Dispatch(route, new StorageCleanupCommand(decision.Preview.Cleanup!.Kind, decision.Preview.Cleanup.Revision), cancellationToken: _storageStop.Token).Completion;
            ObserveTransfer(_storageApply);
        }
        if (_storageApply is { IsCompleted: true } applying)
        {
            _storageApply = null; _storageStatus = null; _storageStatusUnavailable = false; changed = true;
            if (PendingQuery.Succeeded(applying))
            {
                if (_storageApplyingMove) _closeForStorageMigration?.Invoke();
                else _feedback.Info(_storageApplyingCancel ? "待迁移已取消。" : "符合条件的项目已清理。");
            }
            else if (!applying.IsCanceled) _feedback.Error(applying.IsCompletedSuccessfully ? applying.Result.Error?.Message ?? "存储操作未完成。" : "存储操作未完成，请重新预览。");
        }
        if (changed) _storageRebuildPending = true;
        // Status completion runs before the intent loop. Keep a clicked source alive until
        // that loop consumes it; an unrelated asynchronous fact refresh must not drop input.
        if (_storageRebuildPending && _catalog is not null && _instanceDirectory is null && _selected == "storage"
            && !_pending.Any(intent => IsStoragePreferencesIntent(intent.Command))) BuildSections();
    }

    private void CancelStoragePreferences()
    {
        Interlocked.Increment(ref _storageGeneration);
        _storageStop?.Cancel(); _storageStop?.Dispose(); _storageStop = null;
        _storagePreviewRead = null;
        // Keep an already dispatched completion observable: after its commit a queued migration must close the host.
        if (_storageDialog != Guid.Empty) _feedback.DismissDialog(_storageDialog);
        _storageDialog = default;
        while (_storageDecisions.TryDequeue(out _)) { }
        int pending = _pending.Count;
        for (int i = 0; i < pending && _pending.TryDequeue(out var intent); i++)
            if (!IsStoragePreferencesIntent(intent.Command)) _pending.Enqueue(intent);
    }
}
