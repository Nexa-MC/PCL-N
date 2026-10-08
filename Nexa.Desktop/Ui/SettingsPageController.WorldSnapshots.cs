using System.Collections.Concurrent;
using System.Globalization;
using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private (string? Instance, string? World) _worldSnapshotTarget;
    private IReadOnlyList<InstanceWorldSnapshot> _worldSnapshotRows = [];
    private Task<XsrResult<IReadOnlyList<InstanceWorldSnapshot>>>? _worldSnapshotRead;
    private Task<XsrResult<InstanceWorldSnapshotVerification>>? _worldSnapshotVerify;
    private Task<XsrResult>? _worldSnapshotWrite;
    private CancellationTokenSource? _worldSnapshotStop;
    private readonly ConcurrentQueue<(long Generation, bool Accepted, InstanceWorldSnapshotRestoreCommand Command)> _worldSnapshotDecisions = new();
    private Guid _worldSnapshotDialog;
    private long _worldSnapshotGeneration;
    private string? _worldSnapshotMessage;
    private bool WorldSnapshotBusy => _worldSnapshotRead is not null || _worldSnapshotVerify is not null || _worldSnapshotWrite is not null || _worldSnapshotDialog != Guid.Empty;

    private void BuildWorldSnapshots(XsrUiEntityId parent, InstanceContentEntry item)
    {
        if (item.World is not { } world || !_queries.TryResolve(InstanceWorldSnapshotContract.List, out _)) return;
        var target = (Instance: _instance, World: item.Name);
        if (_worldSnapshotTarget != target) { CancelWorldSnapshots(); _worldSnapshotTarget = target; }
        var group = FormGroup(parent, "WorldSnapshots", "世界快照");
        Text(group, "手动保存不可变快照；校验后可恢复为新的世界。原世界保持原状，运行中的游戏会拒绝操作。", 11, Muted, 40);
        var actions = Stack(group, "WorldSnapshots.Actions", XsrUiOrientation.Horizontal, 8);
        ManagementButton(actions, "读取快照历史", BeginWorldSnapshotRead, 124);
        ManagementButton(actions, "创建世界快照", () => BeginWorldSnapshotWrite(InstanceWorldSnapshotContract.Capture,
            new InstanceWorldSnapshotCaptureCommand(target.Instance!, target.World, world.Revision)), 124);
        if (WorldSnapshotBusy) Text(group, "正在处理世界快照…", 11, Muted, 28);
        foreach (var snapshot in _worldSnapshotRows.Take(16))
        {
            var row = Stack(group, "WorldSnapshots." + snapshot.Identity, XsrUiOrientation.Vertical, 6);
            var caption = Text(row, snapshot.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                + " · " + snapshot.Files.ToString(CultureInfo.InvariantCulture) + " " + _shell.Renderer.LocalizeText("个文件")
                + " · " + snapshot.Bytes.ToString(CultureInfo.InvariantCulture) + " B", 11, Muted, 26);
            DesktopLiteralText.Preserve(_shell.Tree, caption);
            var buttons = Stack(row, "WorldSnapshots.RowActions", XsrUiOrientation.Horizontal, 8);
            ManagementButton(buttons, "校验快照", () => BeginWorldSnapshotVerify(snapshot.Identity), 96);
            ManagementButton(buttons, "恢复为新世界", () => ConfirmWorldSnapshotRestore(snapshot, world.Revision), 124);
        }
        if (_worldSnapshotRows.Count > 16) Text(group, "显示最近 16 个世界快照。", 11, Muted, 26);
        if (_worldSnapshotMessage is { } message)
        { var text = Text(group, message, 11, Muted, 48); _shell.Tree.GetComponent<XsrUiVisualStyle>(text)!.WrapText = true; DesktopLiteralText.Preserve(_shell.Tree, text); }
    }

    private bool WorldSnapshotTargetCurrent => _visible && _selected == "saves" && _instance == _worldSnapshotTarget.Instance
        && _contentDetail?.Name == _worldSnapshotTarget.World && _worldSnapshotTarget.Instance is not null;

    private void BeginWorldSnapshotRead()
    {
        if (!WorldSnapshotTargetCurrent || WorldSnapshotBusy || !_queries.TryResolve(InstanceWorldSnapshotContract.List, out var route)) return;
        _worldSnapshotStop ??= new();
        _worldSnapshotRead = _queries.QueryAsync<InstanceWorldSnapshotListQuery, IReadOnlyList<InstanceWorldSnapshot>>(route,
            new(_worldSnapshotTarget.Instance!, _worldSnapshotTarget.World!), cancellationToken: _worldSnapshotStop.Token).AsTask();
        WakeOnPlatformCompletion(_worldSnapshotRead); BuildSections(true);
    }

    private void BeginWorldSnapshotVerify(string identity)
    {
        if (!WorldSnapshotTargetCurrent || WorldSnapshotBusy || !_queries.TryResolve(InstanceWorldSnapshotContract.Verify, out var route)) return;
        _worldSnapshotStop ??= new();
        _worldSnapshotVerify = _queries.QueryAsync<InstanceWorldSnapshotVerifyQuery, InstanceWorldSnapshotVerification>(route,
            new(_worldSnapshotTarget.Instance!, _worldSnapshotTarget.World!, identity), cancellationToken: _worldSnapshotStop.Token).AsTask();
        WakeOnPlatformCompletion(_worldSnapshotVerify); BuildSections(true);
    }

    private void BeginWorldSnapshotWrite<T>(XsrSemanticId semantic, T command) where T : notnull
    {
        if (!WorldSnapshotTargetCurrent || WorldSnapshotBusy || _managementWrite is not null || !_commands.TryResolve(semantic, out var route)) return;
        _worldSnapshotStop ??= new();
        _worldSnapshotWrite = _commands.Dispatch(route, command, cancellationToken: _worldSnapshotStop.Token).Completion;
        WakeOnPlatformCompletion(_worldSnapshotWrite); BuildSections(true);
    }

    private void ConfirmWorldSnapshotRestore(InstanceWorldSnapshot snapshot, string revision)
    {
        if (!WorldSnapshotTargetCurrent || WorldSnapshotBusy || _managementWrite is not null) return;
        string name = "Snapshot-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + snapshot.Identity[..8];
        var command = new InstanceWorldSnapshotRestoreCommand(_worldSnapshotTarget.Instance!, _worldSnapshotTarget.World!, snapshot.Identity, name, revision);
        long generation = _worldSnapshotGeneration;
        string description = string.Format(CultureInfo.InvariantCulture, _shell.Renderer.LocalizeText("将校验 {0} 个文件（{1} 字节），恢复到新世界：{2}。\n现有世界不会被覆盖。"), snapshot.Files, snapshot.Bytes, name);
        _worldSnapshotDialog = _feedback.ShowDialog("settings.world-snapshot", "恢复世界快照", description, "恢复副本", "返回",
            accepted => { if (Volatile.Read(ref _worldSnapshotGeneration) == generation) _worldSnapshotDecisions.Enqueue((generation, accepted, command)); });
    }

    private void UpdateWorldSnapshots()
    {
        if (!WorldSnapshotTargetCurrent) { if (_worldSnapshotTarget.Instance is not null) CancelWorldSnapshots(); return; }
        bool changed = false;
        while (_worldSnapshotDecisions.TryDequeue(out var decision))
        {
            if (decision.Generation != _worldSnapshotGeneration) continue;
            _worldSnapshotDialog = default;
            if (decision.Accepted) BeginWorldSnapshotWrite(InstanceWorldSnapshotContract.Restore, decision.Command);
            changed = true;
        }
        if (_worldSnapshotRead is { IsCompleted: true } read)
        {
            _worldSnapshotRead = null; changed = true;
            if (PendingQuery.Succeeded(read)) { _worldSnapshotRows = read.Result.Value!; _worldSnapshotMessage = _worldSnapshotRows.Count == 0 ? _shell.Renderer.LocalizeText("此世界尚无快照。") : null; }
            else if (!read.IsCanceled) _worldSnapshotMessage = _shell.Renderer.LocalizeText("无法读取世界快照历史，请检查存储目录。");
        }
        if (_worldSnapshotVerify is { IsCompleted: true } verify)
        {
            _worldSnapshotVerify = null; changed = true;
            if (PendingQuery.Succeeded(verify))
            { var report = verify.Result.Value!; _worldSnapshotMessage = report.Ready ? _shell.Renderer.LocalizeText("世界快照全部文件校验通过。") : _shell.Renderer.LocalizeText("世界快照存在缺失或损坏文件：") + "\n" + string.Join('\n', report.MissingOrCorruptPaths.Take(8)); }
            else if (!verify.IsCanceled) _worldSnapshotMessage = _shell.Renderer.LocalizeText("世界快照校验未完成。");
        }
        if (_worldSnapshotWrite is { IsCompleted: true } write)
        {
            _worldSnapshotWrite = null; changed = true;
            if (PendingQuery.Succeeded(write)) { _worldSnapshotMessage = _shell.Renderer.LocalizeText("世界快照操作已完成。"); BeginWorldSnapshotRead(); RefreshManagement(); ManagementChanged?.Invoke(); }
            else if (!write.IsCanceled) _worldSnapshotMessage = write.IsCompletedSuccessfully ? write.Result.Error?.Message ?? _shell.Renderer.LocalizeText("世界快照操作未完成。") : _shell.Renderer.LocalizeText("世界快照操作未完成。");
        }
        if (changed) BuildSections(true);
    }

    private void CancelWorldSnapshots()
    {
        Interlocked.Increment(ref _worldSnapshotGeneration); _worldSnapshotStop?.Cancel(); _worldSnapshotStop?.Dispose(); _worldSnapshotStop = null;
        _worldSnapshotRead = null; _worldSnapshotVerify = null; _worldSnapshotWrite = null; _worldSnapshotRows = []; _worldSnapshotTarget = default; _worldSnapshotMessage = null;
        if (_worldSnapshotDialog != Guid.Empty) _feedback.DismissDialog(_worldSnapshotDialog); _worldSnapshotDialog = default;
        while (_worldSnapshotDecisions.TryDequeue(out _)) { }
    }
}
