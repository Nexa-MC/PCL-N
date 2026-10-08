using System.Globalization;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal readonly record struct RuntimeDiagnosticCapture(XsrUiRendererDiagnostics Renderer,
    AvaloniaUiRuntimeCapture Native, bool ReducedMotion, bool OptionalMotionSuspended);

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId RefreshRuntimeDiagnostics = XsrSemanticId.Parse("ui.settings.diagnostics.runtime.refresh");
    private readonly HashSet<XsrUiEntityId> _runtimeDiagnosticActions = [];
    private readonly Dictionary<string, XsrUiEntityId> _runtimeDiagnosticBodies = [];
    private RuntimeDiagnosticCapture? _runtimeDiagnosticCapture;
    private bool RuntimeDiagnosticsVisible => !_disposed && _visible && _instanceDirectory is null && _developer && _selected == "appearance";
    private static bool IsRuntimeDiagnosticsIntent(XsrSemanticId command) => command == RefreshRuntimeDiagnostics;

    internal static RuntimeDiagnosticCapture CaptureRuntimeDiagnostics(XsrUiShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        return new(shell.Renderer.CaptureDiagnostics(), AvaloniaUiRuntimeDiagnostics.Capture(),
            shell.Renderer.ReducedMotion, shell.Renderer.OptionalMotionSuspended);
    }

    private void ResetRuntimeDiagnosticControls()
    {
        _runtimeDiagnosticActions.Clear();
        _runtimeDiagnosticBodies.Clear();
    }

    private void RetireRuntimeDiagnostics()
    {
        ResetRuntimeDiagnosticControls();
        _runtimeDiagnosticCapture = null;
    }

    private void BuildRuntimeDiagnostics()
    {
        if (!RuntimeDiagnosticsVisible) { RetireRuntimeDiagnostics(); return; }
        bool Available(string id) => _catalog!.Entries.Any(entry => entry.Id == id && entry.DeveloperOnly
            && entry.Kind == SettingsCatalogEntryKind.State && entry.Availability == SettingsCapabilityAvailability.Available);
        _runtimeDiagnosticCapture ??= CaptureRuntimeDiagnostics(_shell);
        void Card(string id, string key, string title)
        {
            if (!Available(id)) return;
            var card = SettingsCard("SettingsRuntime." + key, new(20, 14, 20, 14), 8);
            var toolbar = Stack(card, "SettingsRuntime." + key + ".Toolbar", XsrUiOrientation.Horizontal, 12);
            var caption = Text(toolbar, title, 14, Ink, 36, 600);
            _shell.Tree.GetComponent<XsrUiElement>(caption)!.Weight = 1;
            var refresh = RefreshIcon(toolbar, "SettingsRuntime." + key + ".Refresh", RefreshRuntimeDiagnostics);
            _shell.Tree.GetComponent<XsrUiSemantic>(refresh)!.Label = "捕获运行诊断";
            _runtimeDiagnosticActions.Add(refresh);
            _runtimeDiagnosticBodies[key] = Stack(card, "SettingsRuntime." + key + ".Body", XsrUiOrientation.Vertical, 4);
        }
        Card("global.appearance.d999201ab2ad", "Renderer", "UI Renderer 信息");
        Card("global.appearance.6357798f5b55", "LayoutPaint", "Layout / Paint 调试");
        Card("global.appearance.d44cc0e413d3", "Scheduler", "Animation Scheduler");
        Card("global.appearance.d1957652c919", "Performance", "UI 性能指标");
        BuildRuntimeDiagnosticFields();
    }

    private void BuildRuntimeDiagnosticFields()
    {
        if (_runtimeDiagnosticCapture is not { } capture) return;
        foreach (var body in _runtimeDiagnosticBodies.Values)
            foreach (var child in _shell.Tree.Children(body).ToArray()) _shell.Tree.Destroy(child);
        string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        string Known(long? value) => value.HasValue ? Number(value.Value) : _shell.Renderer.LocalizeText("不可用");
        void Field(string key, string name, string label, string value)
        {
            if (!_runtimeDiagnosticBodies.TryGetValue(key, out var body)) return;
            var row = Stack(body, "SettingsRuntime." + key + "." + name, XsrUiOrientation.Horizontal, 12);
            var caption = Text(row, label, 12, Muted, 26);
            _shell.Tree.GetComponent<XsrUiElement>(caption)!.Width = 180;
            var text = DiagnosticText(row, "SettingsRuntime." + key + "." + name + ".Value", value, 12, Ink, 26);
            _shell.Tree.GetComponent<XsrUiElement>(text)!.Weight = 1;
        }
        var renderer = capture.Renderer;
        var native = capture.Native;
        Field("Renderer", "SceneVersion", "场景版本", Number(renderer.SceneVersion));
        Field("Renderer", "SceneNodes", "场景节点数量", Number(renderer.SceneNodes));
        Field("Renderer", "TreeCount", "树实体数量", Number(renderer.TreeEntities));
        Field("Renderer", "Scope", "采样范围", _shell.Renderer.LocalizeText("当前渲染器，上一完成帧"));
        Field("LayoutPaint", "LayoutVisits", "上次布局访问", Number(renderer.LayoutVisits));
        Field("LayoutPaint", "SceneBuilds", "场景重建次数", Number(renderer.SceneVersion));
        Field("LayoutPaint", "NativeCommits", "原生场景提交次数", Number(native.CommittedScenes));
        Field("LayoutPaint", "NativePaints", "原生节点绘制回调次数", Number(native.NodePaintCallbacks));
        Field("LayoutPaint", "Scope", "原生计数范围", _shell.Renderer.LocalizeText("当前进程累计提交，不代表物理呈现"));
        Field("Scheduler", "PendingState", "待合并状态数量", Number(renderer.PendingStateEntries));
        Field("Scheduler", "ActiveTracks", "原生活动动画轨道", Number(native.ActiveMotionTracks));
        Field("Scheduler", "ClockRunning", "动画时钟", _shell.Renderer.LocalizeText(native.MotionTimerRunning ? "运行中" : "已停止"));
        Field("Scheduler", "TargetRate", "动画时钟目标频率", Number(native.MotionTargetFramesPerSecond) + " Hz");
        Field("Scheduler", "ObservedRate", "最近两秒时钟观测频率", native.MotionTickRate is { } rate
            ? rate.ToString("0.0", CultureInfo.InvariantCulture) + " Hz" : _shell.Renderer.LocalizeText("不可用"));
        Field("Scheduler", "MotionPolicy", "动态策略", _shell.Renderer.LocalizeText(capture.ReducedMotion || capture.OptionalMotionSuspended ? "减少动态效果" : "正常动态效果"));
        Field("Performance", "PixelCharge", "解码图像像素计费字节", Number(native.RasterPixelChargeBytes));
        Field("Performance", "AdmissionBudget", "图像准入预算字节", Number(native.RasterAdmissionBudgetBytes));
        Field("Performance", "RasterEntries", "保留图像数量", Number(native.RasterEntries));
        Field("Performance", "RasterLeases", "活动图像租约数量", Number(native.RasterLeases));
        Field("Performance", "DecodeAttempts", "图像解码尝试次数", Number(native.RasterDecodeAttempts));
        Field("Performance", "DisposedBitmaps", "已释放位图数量", Number(native.RasterDisposedBitmaps));
        Field("Performance", "MemoryAvailable", "系统可用内存字节", Known(native.MemoryPressure.AvailableBytes));
        Field("Performance", "MemoryLimit", "系统内存上限字节", Known(native.MemoryPressure.LimitBytes));
        Field("Performance", "GpuResident", "已测量 GPU 驻留字节", Known(native.MemoryPressure.GpuResidentBytes));
        Field("Performance", "MemorySource", "内存采样来源", native.MemoryPressure.Source is { Length: > 0 } source ? source : "unavailable");
        Field("Performance", "PresentationRate", "物理呈现帧率", _shell.Renderer.LocalizeText("不可用"));
        foreach (var body in _runtimeDiagnosticBodies.Values) _shell.Tree.MarkDirty(body, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private void HandleRuntimeDiagnosticsIntent(DesktopUiIntent intent)
    {
        if (!RuntimeDiagnosticsVisible || _store.Read<long>(_revisionId).Value != _revision
            || intent.Command != RefreshRuntimeDiagnostics || !_shell.Tree.IsAlive(intent.Source)
            || !_runtimeDiagnosticActions.Contains(intent.Source)
            || _shell.Tree.GetComponent<XsrUiInput>(intent.Source)?.Enabled != true) return;
        _runtimeDiagnosticCapture = CaptureRuntimeDiagnostics(_shell);
        BuildRuntimeDiagnosticFields();
    }
}
