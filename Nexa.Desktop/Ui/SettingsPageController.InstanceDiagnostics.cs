using System.Globalization;
using Nexa.Core;
using Nexa.Services.Minecraft.Process;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private sealed record InstanceRuntimeCapture(MinecraftProcessSnapshot? Session, JvmHostObservation? Observation, JvmRunSample? Sample);
    private InstanceRuntimeCapture? _instanceDiagnosticCapture;
    private string? _instanceDiagnosticTarget;

    private void CancelInstanceDiagnostics()
    {
        _instanceDiagnosticCapture = null; _instanceDiagnosticTarget = null;
        CancelInstanceDiagnosticHistory();
    }

    private InstanceRuntimeCapture CaptureInstanceDiagnostics(string instance)
    {
        IReadOnlyList<T> Read<T>(XsrSemanticId key) => _store.TryResolve(key, out var id) ? _store.ReadCollection<T>(id).Items : [];
        var session = Read<MinecraftProcessSnapshot>(MinecraftProcessStateComposition.SessionsKey)
            .Where(item => PathIdentity.Comparer.Equals(item.InstanceDirectory, instance)).OrderByDescending(item => item.StartedAt).FirstOrDefault();
        if (session is null) return new(null, null, null);
        var observation = Read<JvmHostObservation>(JvmHostStateContract.ObservationsKey).FirstOrDefault(item => item.SessionId == session.SessionId);
        var sample = Read<JvmRunSample>(JvmHostStateContract.SamplesKey).Where(item => item.SessionId == session.SessionId).OrderByDescending(item => item.Sequence).FirstOrDefault();
        return new(session, observation, sample);
    }

    private void BuildInstanceDiagnostics()
    {
        if (_instance is null) return;
        if (_instanceDiagnosticTarget != _instance) { CancelInstanceDiagnostics(); _instanceDiagnosticTarget = _instance; }
        _instanceDiagnosticCapture ??= CaptureInstanceDiagnostics(_instance);
        var toolbar = Stack(_sections, "InstanceDiagnostics.Toolbar", XsrUiOrientation.Horizontal, 10);
        ManagementButton(toolbar, "刷新实例诊断", () => { CancelInstanceDiagnostics(); BuildSections(true); }, 144);
        ManagementButton(toolbar, _instanceOperationView ? "查看运行历史" : "查看操作阶段", () => { _instanceOperationView = !_instanceOperationView; BuildSections(true); }, 144);
        if (ExportDiagnostics is not null) ManagementButton(toolbar, "导出诊断包", () => _ = ExportDiagnosticsAsync(), 144);
        Text(_sections, "仅显示此实例的捕获事实；点击刷新更新。缺少采样时显示不可用。", 12, Muted, 36);
        var capture = _instanceDiagnosticCapture;
        var status = FormGroup(_sections, "InstanceDiagnostics.Status", "当前状态");
        if (capture.Session is not { } session) Text(status, "此实例尚无当前进程记录。", 13, Muted, 30);
        else
        {
            InstanceDiagnosticFact(status, "Session", "运行身份", session.SessionId.ToString("N"));
            InstanceDiagnosticFact(status, "State", "进程状态", _shell.Renderer.LocalizeText(session.State switch { MinecraftProcessState.Created => "已创建", MinecraftProcessState.Running => "运行中", MinecraftProcessState.Exited => "已退出", MinecraftProcessState.Cancelled => "已取消", _ => "失败" }));
            InstanceDiagnosticFact(status, "Window", "游戏窗口", _shell.Renderer.LocalizeText(session.GameWindowConfirmed ? "已检测到" : "未确认"));
            if (session.ExitCode is { } exit) InstanceDiagnosticFact(status, "ExitCode", "退出码", exit.ToString(CultureInfo.InvariantCulture));
        }
        BuildInstanceLaunchPerformance(capture);
        BuildInstanceResourceAnalysis(capture);
        BuildInstanceSystemCorrelation(capture);
        BuildInstanceDiagnosticHistory();
    }

    private void InstanceDiagnosticFact(XsrUiEntityId parent, string key, string label, string value)
    {
        var row = Stack(parent, "InstanceDiagnostics." + key, XsrUiOrientation.Horizontal, 12);
        _shell.Tree.GetComponent<XsrUiElement>(Text(row, label, 12, Muted, 28))!.Width = 180;
        var text = DiagnosticText(row, "InstanceDiagnostics." + key + ".Value", value, 12, Ink, 28);
        _shell.Tree.GetComponent<XsrUiElement>(text)!.Weight = 1;
    }

    private void BuildInstanceLaunchPerformance(InstanceRuntimeCapture capture)
    {
        var performance = FormGroup(_sections, "InstanceDiagnostics.Launch", "启动性能");
        string Unknown() => _shell.Renderer.LocalizeText("不可用");
        InstanceDiagnosticFact(performance, "StartedAt", "进程创建时间", capture.Session?.StartedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture) ?? Unknown());
        InstanceDiagnosticFact(performance, "LaunchDuration", "进程启动耗时", capture.Observation is { } observation && observation.LaunchDurationMilliseconds >= 0 ? observation.LaunchDurationMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms" : Unknown());
        InstanceDiagnosticFact(performance, "LaunchWindow", "启动采样窗口", capture.Observation is { LaunchWindowMilliseconds: > 0 } window ? window.LaunchWindowMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms" : Unknown());
        Text(performance, "进程耗时与前 30 秒采样不代表进入世界耗时或 Minecraft 帧率。", 11, Muted, 36);
    }

    private void BuildInstanceResourceAnalysis(InstanceRuntimeCapture capture)
    {
        var resources = FormGroup(_sections, "InstanceDiagnostics.Resources", "资源分析");
        string Unknown() => _shell.Renderer.LocalizeText("不可用");
        void Bytes(string key, string label, long? bytes) => InstanceDiagnosticFact(resources, key, label, bytes is >= 0 ? FormatContentSize(bytes.Value) : Unknown());
        var observation = capture.Observation;
        Bytes("Physical", "进程工作集峰值", observation is { CoreMetricsObserved: true } ? observation.PeakWorkingSetBytes : null);
        Bytes("Private", "进程私有内存峰值", observation is { CoreMetricsObserved: true } ? observation.PeakPrivateBytes : null);
        Bytes("Heap", "已测堆峰值", observation?.MeasuredHeapPeakBytes); Bytes("Native", "已测 native 峰值", observation?.MeasuredNativePeakBytes);
        Bytes("Commit", "已测提交量峰值", observation?.MeasuredCommitPeakBytes); Bytes("GpuLocal", "已测 GPU 专用峰值", observation?.MeasuredGpuLocalPeakBytes);
        Bytes("GpuShared", "已测 GPU 共享峰值", observation?.MeasuredGpuSharedPeakBytes); Bytes("TreePhysical", "进程树工作集峰值", observation?.PeakTreeWorkingSetBytes);
        Bytes("PhysicalP95", "工作集 P95", observation is { CoreMetricsObserved: true } ? observation.RuntimePhysicalP95Bytes : null);
        InstanceDiagnosticFact(resources, "CpuPeak", "已测 CPU 峰值", observation is { CpuPercentObserved: true } ? observation.CpuPeakPercent.ToString(CultureInfo.InvariantCulture) + "%" : Unknown());
        if (capture.Sample is { SampleCount: > 0 } sample)
        {
            InstanceDiagnosticFact(resources, "SampleCount", "窗口采样次数", sample.SampleCount.ToString(CultureInfo.InvariantCulture));
            InstanceDiagnosticFact(resources, "SampleWindow", "窗口长度", sample.WindowMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
            InstanceDiagnosticFact(resources, "WorkingMean", "窗口工作集均值", double.IsFinite(sample.WorkingMeanMiB) && sample.WorkingMeanMiB >= 0 ? sample.WorkingMeanMiB.ToString("0.##", CultureInfo.InvariantCulture) + " MiB" : Unknown());
            InstanceDiagnosticFact(resources, "CpuMean", "窗口 CPU 均值", double.IsFinite(sample.CpuMeanPercent) && sample.CpuMeanPercent >= 0 ? sample.CpuMeanPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%" : Unknown());
        }
        else Text(resources, "此实例尚无可用的运行采样窗口。", 12, Muted, 30);
    }

    private void BuildInstanceSystemCorrelation(InstanceRuntimeCapture capture)
    {
        var system = FormGroup(_sections, "InstanceDiagnostics.System", "系统事件关联");
        if (capture.Observation is not { SystemEventsObserved: true } observation)
        { Text(system, "此运行的系统事件采集不可用。", 12, Muted, 30); return; }
        Text(system, "仅列出该进程运行时段的实际事件，时间相关不能证明崩溃原因。", 11, Muted, 34);
        if (observation.SystemEvents.Count == 0) Text(system, "采集已完成，此时段没有匹配事件。", 12, Muted, 30);
        foreach (string message in observation.SystemEvents.Take(32))
        {
            string bounded = message.Length > 2048 ? message[..2048] + "…" : message;
            var text = DiagnosticText(system, "InstanceDiagnostics.System.Event", bounded, 12, Muted, 0);
            _shell.Tree.GetComponent<XsrUiElement>(text)!.Height = null;
            _shell.Tree.GetComponent<XsrUiText>(text)!.MaxLines = 4;
            _shell.Tree.GetComponent<XsrUiVisualStyle>(text)!.WrapText = true;
        }
    }
}
