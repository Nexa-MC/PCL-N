using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    internal Func<Task<string?>>? SelectDataPackFileAsync { get; set; }
    private Task? _worldPlatformAction;
    private string? _worldPlatformCompletionMessage;
    private Task<string?>? _worldImportPicker;
    private (string Instance, string World, string Revision) _worldImportTarget;
    private Task<XsrResult<InstanceWorldMetadata>>? _worldMetadataRead;
    private (string Instance, string World) _worldMetadataTarget;
    private bool _worldMetadataFailed;

    private void BuildWorldDetails(XsrUiEntityId parent, InstanceContentEntry item)
    {
        BuildWorldHealth(parent, item);
        if (item.World is not { } world)
        {
            var target = (_instance!, item.Name);
            if (_worldMetadataTarget != target) { _worldMetadataTarget = target; _worldMetadataRead = null; _worldMetadataFailed = false; }
            if (!_worldMetadataFailed && _worldMetadataRead is null && _queries.TryResolve(InstanceWorldContract.Read, out var route))
            {
                _worldMetadataRead = _queries.QueryAsync<InstanceWorldMetadataQuery, InstanceWorldMetadata>(route, new(_instance!, item.Name)).AsTask();
                WakeOnPlatformCompletion(_worldMetadataRead);
            }
            Text(parent, _worldMetadataRead is not null ? "正在读取世界元数据…" : "无法读取此世界的 level.dat，已保留文件。", 13, Muted, 28); return;
        }
        ManagementFactIn(parent, "世界名称", world.LevelName, true);
        if (world.DataVersion is { } dataVersion) ManagementFactIn(parent, "数据版本", dataVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (world.LastPlayed is { } played) ManagementFactIn(parent, "上次游玩", played.ToLocalTime().ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.CurrentCulture));
        if (world.Seed is { } seed) ManagementFactIn(parent, "世界种子", seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (world.GameMode is { } mode) ManagementFactIn(parent, "游戏模式", mode switch { 0 => "生存", 1 => "创造", 2 => "冒险", 3 => "旁观", _ => mode.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        if (world.Difficulty is { } difficulty) ManagementFactIn(parent, "难度", difficulty switch { 0 => "和平", 1 => "简单", 2 => "普通", 3 => "困难", _ => difficulty.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        ManagementFactIn(parent, "极限模式", world.Hardcore ? "是" : "否");
        if (!world.SizeComplete) Text(parent, "世界大小超过本次扫描预算，未显示部分总量。", 12, Muted, 28);
        var actions = Stack(parent, "WorldManagementActions", XsrUiOrientation.Horizontal, 10);
        ManagementButton(actions, world.Locked ? "解除编辑保护" : "锁定编辑", () => DispatchWorld(InstanceWorldContract.SetLock,
            new InstanceWorldLockCommand(_instance!, item.Name, !world.Locked, world.Revision)), 116);
        if (_commands.TryResolve(InstanceWorldContract.Backup, out _)) ManagementButton(actions, "备份世界", () => DispatchWorld(InstanceWorldContract.Backup,
            new InstanceWorldBackupCommand(_instance!, item.Name, world.Revision)), 96);
        if (!world.Locked)
        {
            ManagementButton(actions, "复制世界", () => DispatchWorld(InstanceWorldContract.Copy,
                new InstanceWorldCopyCommand(_instance!, item.Name, item.Name + "-copy-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), world.Revision)), 96);
            if (world.DataPacksSupported && SelectDataPackFileAsync is not null) ManagementButton(actions, "导入数据包", () =>
            {
                if (_worldImportPicker is { IsCompleted: false } || _managementWrite is not null) return;
                _worldImportTarget = (_instance!, item.Name, world.Revision); _worldImportPicker = SelectDataPackFileAsync!(); WakeOnPlatformCompletion(_worldImportPicker);
            }, 106);
        }
        BuildWorldSnapshots(parent, item);
        Text(parent, "世界数据包", 16, Ink, 28, 600);
        if (!world.DataPacksSupported) { Text(parent, "此世界的数据版本不支持 Minecraft 数据包。", 13, Muted, 28); return; }
        if (world.DataPacks.Count == 0) Text(parent, "此世界尚未安装数据包。", 13, Muted, 28);
        foreach (var pack in world.DataPacks)
        {
            var row = Stack(parent, "WorldDataPack", XsrUiOrientation.Horizontal, 12);
            var label = Text(row, pack.Name + " · " + _shell.Renderer.LocalizeText(pack.Trashed ? "已移除" : pack.Enabled ? "已启用" : "已停用"), 13, Ink, 36);
            DesktopLiteralText.Preserve(_shell.Tree, label); _shell.Tree.GetComponent<XsrUiElement>(label)!.Weight = 1;
            if (world.Locked) continue;
            if (pack.Trashed)
            {
                ManagementButton(row, "还原数据包", () => DispatchWorld(InstanceWorldContract.RestoreDataPack,
                    new InstanceWorldDataPackRestoreCommand(_instance!, item.Name, pack.TrashName, world.Revision)), 106);
                continue;
            }
            if (pack.Id != "vanilla") ManagementButton(row, pack.Enabled ? "停用" : "启用", () => DispatchWorld(InstanceWorldContract.SetDataPackEnabled,
                new InstanceWorldDataPackCommand(_instance!, item.Name, pack.Id, !pack.Enabled, world.Revision)), 64);
            if (!pack.BuiltIn && !pack.Enabled) ManagementButton(row, "移至回收", () => DispatchWorld(InstanceWorldContract.RemoveDataPack,
                new InstanceWorldDataPackRemoveCommand(_instance!, item.Name, pack.Name, world.Revision)), 90);
        }
        Text(parent, "数据包更改将在下次加载世界时生效。", 12, Muted, 28);
    }

    private void DispatchWorld<T>(XsrSemanticId id, T command) where T : notnull
    {
        if (_instance is null || _managementWrite is not null || !_commands.TryResolve(id, out var route)) return;
        _managementWriteInstance = _instance; _managementWrite = _commands.Dispatch(route, command).Completion; WakeOnPlatformCompletion(_managementWrite);
    }
    private void StartWorldPlatformAction(Func<Task> action, string? completedMessage = null)
    {
        if (_worldPlatformAction is { IsCompleted: false } || _managementWrite is not null) return;
        try { _worldPlatformCompletionMessage = completedMessage; _worldPlatformAction = action(); WakeOnPlatformCompletion(_worldPlatformAction); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { _feedback.Error("世界或截图操作无法启动。"); }
    }
    private void UpdateWorldPlatformAction()
    {
        UpdateWorldHealth();
        if (_worldMetadataRead is { IsCompleted: true } reading)
        {
            _worldMetadataRead = null; _worldMetadataFailed = !PendingQuery.Succeeded(reading);
            if (_instance == _worldMetadataTarget.Instance && _contentDetail?.Name == _worldMetadataTarget.World && _selected == "saves")
            {
                if (!_worldMetadataFailed) _contentDetail = _contentDetail with { World = reading.Result.Value };
                BuildSections(true);
            }
        }
        if (_worldImportPicker is { IsCompleted: true } picker)
        {
            _worldImportPicker = null;
            if (picker.IsCompletedSuccessfully && picker.Result is { } source && _instance == _worldImportTarget.Instance && _contentDetail?.Name == _worldImportTarget.World)
                DispatchWorld(InstanceWorldContract.ImportDataPack, new InstanceWorldDataPackImportCommand(_worldImportTarget.Instance, _worldImportTarget.World, source, _worldImportTarget.Revision));
            else if (!picker.IsCompletedSuccessfully) { _ = picker.Exception; _feedback.Error("无法选择数据包文件。"); }
        }
        if (_worldPlatformAction is not { IsCompleted: true } completed) return;
        _worldPlatformAction = null;
        if (!completed.IsCompletedSuccessfully) { _ = completed.Exception; _feedback.Error("世界或截图操作未完成，请检查访问权限后重试。"); }
        else { if (_worldPlatformCompletionMessage is { } message) _feedback.Info(message); RefreshManagement(); }
        _worldPlatformCompletionMessage = null;
    }
}
