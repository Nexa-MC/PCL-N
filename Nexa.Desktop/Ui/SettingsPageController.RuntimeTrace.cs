using System.Globalization;
using Nexa.Services.Logging;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId RuntimeTraceAction = XsrSemanticId.Parse("ui.settings.diagnostics.trace");
    private readonly Dictionary<XsrUiEntityId, string> _runtimeTraceActions = [];
    private RuntimeTraceSnapshot? _runtimeTraceSnapshot;
    private Task<XsrResult<RuntimeTraceSnapshot>>? _runtimeTraceRead;
    private CancellationTokenSource? _runtimeTraceStop;
    private string _runtimeTraceFilter = "operations";
    private int _runtimeTracePage;
    private XsrUiEntityId _runtimeTraceBody;
    private bool RuntimeTraceVisible => !_disposed && _visible && _instanceDirectory is null && _developer && _selected == "privacy";
    private static bool IsRuntimeTraceIntent(XsrSemanticId command) => command == RuntimeTraceAction;
    private void ResetRuntimeTraceControls() { _runtimeTraceActions.Clear(); _runtimeTraceBody = default; }
    private void RetireRuntimeTrace()
    {
        _runtimeTraceStop?.Cancel(); _runtimeTraceStop?.Dispose(); _runtimeTraceStop = null;
        _runtimeTraceRead = null; _runtimeTraceSnapshot = null; _runtimeTracePage = 0;
        ResetRuntimeTraceControls();
    }
    private void BuildRuntimeTraceDiagnostics()
    {
        if (!RuntimeTraceVisible) { RetireRuntimeTrace(); return; }
        if (!_queries.TryResolve(RuntimeTraceContract.Read, out _)) return;
        var card = SettingsCard("RuntimeTrace", new(20, 14, 20, 14), 8);
        Text(card, "XSR Operation / State / Launch Trace", 14, Ink, 30, 600);
        Text(card, "仅保留当前会话的有界身份、阶段与结果；捕获后翻页沿用快照，不读取请求或状态内容。", 11, Muted, 42);
        var toolbar = Stack(card, "RuntimeTrace.Toolbar", XsrUiOrientation.Horizontal, 8);
        Button("Capture", "捕获 Trace", "refresh"); Button("Operations", "操作", "operations");
        Button("State", "状态", "state"); Button("Launch", "启动", "launch"); Button("Next", "下一页", "next");
        _runtimeTraceBody = Stack(card, "RuntimeTrace.Rows", XsrUiOrientation.Vertical, 4);
        BuildRuntimeTraceRows();
        void Button(string key, string label, string action)
        {
            var entity = ActionButton(toolbar, "RuntimeTrace." + key, label, RuntimeTraceAction, 88);
            _runtimeTraceActions[entity] = action;
            _shell.Tree.GetComponent<XsrUiInput>(entity)!.Enabled = _runtimeTraceRead is null;
        }
    }
    private void UpdateRuntimeTrace()
    {
        if (!RuntimeTraceVisible) { RetireRuntimeTrace(); return; }
        if (_runtimeTraceRead is not { IsCompleted: true } read) return;
        _runtimeTraceRead = null; _runtimeTraceStop?.Dispose(); _runtimeTraceStop = null;
        if (PendingQuery.Succeeded(read)) _runtimeTraceSnapshot = read.Result.Value;
        else if (!read.IsCanceled) _feedback.Error("无法捕获 XSR Trace，请重试。");
        BuildSections();
    }
    private void BuildRuntimeTraceRows()
    {
        if (!_shell.Tree.IsAlive(_runtimeTraceBody)) return;
        foreach (var child in _shell.Tree.Children(_runtimeTraceBody).ToArray()) _shell.Tree.Destroy(child);
        if (_runtimeTraceSnapshot is not { } snapshot)
        { Text(_runtimeTraceBody, _runtimeTraceRead is null ? "点击捕获读取当前 Trace。" : "正在捕获 Trace…", 12, Muted, 28); return; }
        var rows = FilterRuntimeTrace(snapshot, _runtimeTraceFilter);
        int pages = Math.Max(1, (rows.Count + 31) / 32);
        _runtimeTracePage = Math.Clamp(_runtimeTracePage, 0, pages - 1);
        Text(_runtimeTraceBody, $"{_runtimeTracePage + 1} / {pages} · {rows.Count} " + _shell.Renderer.LocalizeText("项")
            + " · " + _shell.Renderer.LocalizeText("环容量") + " " + snapshot.Capacity.ToString(CultureInfo.InvariantCulture)
            + " · " + _shell.Renderer.LocalizeText("已丢弃") + " " + snapshot.Dropped.ToString(CultureInfo.InvariantCulture), 11, Muted, 26);
        int index = 0;
        foreach (var entry in rows.Skip(_runtimeTracePage * 32).Take(32))
        {
            var row = Stack(_runtimeTraceBody, "RuntimeTrace.Entry." + index++, XsrUiOrientation.Vertical, 2);
            DiagnosticText(row, "RuntimeTrace.Identity." + index, entry.Kind + " · " + entry.SemanticId, 12, Ink, 26);
            DiagnosticText(row, "RuntimeTrace.Detail." + index, entry.Detail + " · cid=" + entry.CorrelationId
                + " · ticks=" + entry.MonotonicTimestamp.ToString(CultureInfo.InvariantCulture)
                + " · " + _shell.Renderer.LocalizeText(entry.IsSuccess ? "成功" : "失败"), 11, Muted, 26);
        }
        _shell.Tree.MarkDirty(_runtimeTraceBody, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
    internal static IReadOnlyList<RuntimeTraceEntry> FilterRuntimeTrace(RuntimeTraceSnapshot snapshot, string filter)
        => Array.AsReadOnly(snapshot.Entries.Where(entry => filter switch
        {
            "state" => entry.Kind == "State",
            "launch" => entry.SemanticId.StartsWith("minecraft.", StringComparison.Ordinal)
                || entry.SemanticId.StartsWith("process.", StringComparison.Ordinal),
            _ => entry.Kind != "State",
        }).Take(256).ToArray());
    private void HandleRuntimeTraceIntent(DesktopUiIntent intent)
    {
        if (!RuntimeTraceVisible || _store.Read<long>(_revisionId).Value != _revision
            || intent.Command != RuntimeTraceAction || !_shell.Tree.IsAlive(intent.Source)
            || !_runtimeTraceActions.TryGetValue(intent.Source, out string? action)
            || _shell.Tree.GetComponent<XsrUiInput>(intent.Source)?.Enabled != true || _runtimeTraceRead is not null) return;
        if (action == "refresh" && _queries.TryResolve(RuntimeTraceContract.Read, out var route))
        {
            _runtimeTraceStop?.Dispose(); _runtimeTraceStop = new(); _runtimeTracePage = 0;
            _runtimeTraceRead = _queries.QueryAsync<RuntimeTraceQuery, RuntimeTraceSnapshot>(route, new(), cancellationToken: _runtimeTraceStop.Token).AsTask();
            WakeOnPlatformCompletion(_runtimeTraceRead); BuildSections();
        }
        else if (action == "next") { _runtimeTracePage++; BuildRuntimeTraceRows(); }
        else if (action is "operations" or "state" or "launch") { _runtimeTraceFilter = action; _runtimeTracePage = 0; BuildRuntimeTraceRows(); }
    }
}
