using System.Diagnostics;
using System.Globalization;
using Nexa.Services.Logging;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Settings;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    internal bool LauncherSafeMode { get; set; }
    internal Func<bool>? OpenAdvancedCommandPalette { get; set; }
    internal Action<DesktopCommandRoute>? NavigateAdvancedCommand { get; set; }
    internal Func<string, Task>? CopyAdvancedSettingsTextAsync { get; set; }
    internal Action? OpenAdvancedSettingsFile { get; set; }
    private RuntimeDiagnosticCapture? _advancedRuntime;
    private string[]? _advancedSettingsRows;
    private int _advancedSettingsPage;
    private Task<double>? _advancedDelayRead;
    private CancellationTokenSource? _advancedDelayStop;
    private double? _advancedDelayMilliseconds;
    private bool AdvancedWorkspaceVisible => !_disposed && _visible && _instanceDirectory is null && _selected == "advanced";

    private void CancelAdvancedWorkspace()
    {
        _advancedDelayStop?.Cancel(); _advancedDelayStop?.Dispose(); _advancedDelayStop = null;
        _advancedDelayRead = null; _advancedDelayMilliseconds = null;
        _advancedSettingsRows = null; _advancedSettingsPage = 0; _advancedRuntime = null;
    }

    private void UpdateAdvancedWorkspace()
    {
        if (!AdvancedWorkspaceVisible) { CancelAdvancedWorkspace(); return; }
        if (!_developer)
        {
            _advancedDelayStop?.Cancel(); _advancedDelayStop?.Dispose(); _advancedDelayStop = null;
            _advancedDelayRead = null; _advancedDelayMilliseconds = null;
            _advancedSettingsRows = null; _advancedSettingsPage = 0; _advancedRuntime = null;
        }
        if (_advancedDelayRead is not { IsCompleted: true } completed) return;
        _advancedDelayRead = null; _advancedDelayStop?.Dispose(); _advancedDelayStop = null;
        if (completed.IsCompletedSuccessfully) _advancedDelayMilliseconds = completed.Result;
        else _ = completed.Exception;
        BuildSections(true);
    }

    private void BuildAdvancedWorkspace()
    {
        if (_instanceDirectory is not null || _selected != "advanced") return;
        var navigation = FormGroup(_sections, "AdvancedWorkspace.Navigation", "自动化与安全模式");
        Text(navigation, "导航命令只切换页面；安全模式在下次启动时使用 --safe-mode 指定。", 11, Muted, 34);
        Text(navigation, LauncherSafeMode ? "当前会话：安全模式" : "当前会话：普通模式", 12, Ink, 28);
        Button(navigation, "AdvancedWorkspace.Palette", "打开命令面板", () =>
        { if (OpenAdvancedCommandPalette?.Invoke() != true) _feedback.Info("请先完成当前对话框，再打开命令面板。"); }, OpenAdvancedCommandPalette is not null);
        foreach (var route in DesktopCommandLine.Commands)
        {
            var row = Stack(navigation, "AdvancedWorkspace.Route." + route.Id, XsrUiOrientation.Horizontal, 8);
            DesktopLiteralText.Preserve(_shell.Tree, Text(row, "--command=" + route.Id + " · nexacl://" + route.Id, 11, Muted, 28));
            Button(row, "AdvancedWorkspace.Go." + route.Id, route.Label, () => NavigateAdvancedCommand?.Invoke(route), NavigateAdvancedCommand is not null);
        }
        if (!_developer) return;
        if (_advancedRuntime is not { } capture) { capture = CaptureRuntimeDiagnostics(_shell); _advancedRuntime = capture; }
        var diagnostics = FormGroup(_sections, "AdvancedWorkspace.Diagnostics", "高级运行事实");
        Button(diagnostics, "AdvancedWorkspace.Capture", "捕获高级状态", () => { _advancedRuntime = CaptureRuntimeDiagnostics(_shell); BuildSections(true); });
        Fact(diagnostics, "开发者模式", _developer ? "true" : "false");
        Fact(diagnostics, "场景节点数量", capture.Renderer.SceneNodes.ToString(CultureInfo.InvariantCulture));
        Fact(diagnostics, "原生活动动画轨道", capture.Native.ActiveMotionTracks.ToString(CultureInfo.InvariantCulture));
        Fact(diagnostics, "动画时钟", _shell.Renderer.LocalizeText(capture.Native.MotionTimerRunning ? "运行中" : "已停止"));
        Fact(diagnostics, "图像准入预算字节", capture.Native.RasterAdmissionBudgetBytes.ToString(CultureInfo.InvariantCulture));
        var flags = FormGroup(_sections, "AdvancedWorkspace.Flags", "已接入的内部能力");
        Feature("Java 诊断", _queries.TryResolve(JavaRuntimeDiagnosticsContract.Properties, out _));
        Feature("XSR Trace", _queries.TryResolve(RuntimeTraceContract.Read, out _));
        Feature("受保护自动更新", _automaticUpdates is not null);
        Feature("命令面板", OpenAdvancedCommandPalette is not null);
        Text(flags, "这些是当前组合根的只读能力事实，不增加隐藏的可变开关。", 11, Muted, 34);
        var scheduler = FormGroup(_sections, "AdvancedWorkspace.Delay", "调度延时探测");
        Button(scheduler, "AdvancedWorkspace.Delay.Start", "运行 200 ms 调度探测", () =>
        {
            if (_advancedDelayRead is not null) return;
            _advancedDelayStop?.Dispose(); _advancedDelayStop = new();
            _advancedDelayRead = ProbeAdvancedDelayAsync(_advancedDelayStop.Token); ObserveTransfer(_advancedDelayRead); BuildSections(true);
        }, _advancedDelayRead is null);
        if (_advancedDelayRead is not null) Text(scheduler, "正在等待异步调度探测…", 12, Muted, 28);
        if (_advancedDelayMilliseconds is { } measured) Fact(scheduler, "实际调度耗时", measured.ToString("0.##", CultureInfo.InvariantCulture) + " ms");
        var settings = FormGroup(_sections, "AdvancedWorkspace.Settings", "只读脱敏设置快照");
        Text(settings, "只显示有效设置的类型、来源和公开值；文本、路径、凭据与命令内容保持脱敏。", 11, Muted, 36);
        Text(settings, "本地设置文件可能包含路径、账户和凭据。仅在本机编辑器中打开，请勿公开分享。", 11, Muted, 38);
        Button(settings, "AdvancedWorkspace.Settings.OpenFile", "打开 settings.json", () => OpenAdvancedSettingsFile?.Invoke(), OpenAdvancedSettingsFile is not null);
        Button(settings, "AdvancedWorkspace.Settings.Read", "捕获设置快照", () =>
        {
            if (_values is null) return;
            _advancedSettingsRows = CaptureRedactedAdvancedSettings(_values); _advancedSettingsPage = 0; BuildSections(true);
        }, _values is not null);
        if (_advancedSettingsRows is not { } rows) return;
        int pages = Math.Max(1, (rows.Length + 23) / 24); _advancedSettingsPage = Math.Clamp(_advancedSettingsPage, 0, pages - 1);
        foreach (string line in rows.Skip(_advancedSettingsPage * 24).Take(24)) DiagnosticText(settings, "AdvancedWorkspace.Settings.Row", line, 11, Muted, 28);
        if (pages > 1) Button(settings, "AdvancedWorkspace.Settings.Next", "下一页", () => { _advancedSettingsPage = (_advancedSettingsPage + 1) % pages; BuildSections(true); });
        if (CopyAdvancedSettingsTextAsync is not null) Button(settings, "AdvancedWorkspace.Settings.Copy", "复制脱敏设置快照", () =>
        { if (CopyAdvancedSettingsTextAsync is not null) ObserveTransfer(CopyAdvancedSettingsTextAsync(string.Join('\n', rows))); });

        void Feature(string label, bool available) => Fact(flags, label, _shell.Renderer.LocalizeText(available ? "已接入" : "未接入"));
        void Fact(XsrUiEntityId parent, string label, string value) => ManagementFactIn(parent, label, value, true);
        void Button(XsrUiEntityId parent, string name, string caption, Action action, bool enabled = true)
        {
            var entity = ActionButton(parent, name, caption, ManagementAction, 180);
            _shell.Tree.GetComponent<XsrUiInput>(entity)!.Enabled = enabled;
            _managementActions[entity] = () =>
            {
                if (AdvancedWorkspaceVisible && _shell.Tree.IsAlive(entity)
                    && _shell.Tree.GetComponent<XsrUiInput>(entity)?.Enabled == true
                    && (name.StartsWith("AdvancedWorkspace.Go.", StringComparison.Ordinal) || name == "AdvancedWorkspace.Palette" || _developer)) action();
            };
        }
    }

    internal static string[] CaptureRedactedAdvancedSettings(SettingsEffectiveSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Values.Where(item => SettingsPolicySchema.ByKey.ContainsKey(item.Key)).OrderBy(item => item.Key, StringComparer.Ordinal).Take(256).Select(item =>
        {
            var definition = SettingsPolicySchema.ByKey[item.Key];
            bool expose = item.ValidationError is null && definition.Validate(item.Value) is null
                && definition.Kind is SettingsValueKind.Boolean or SettingsValueKind.Number or SettingsValueKind.Enum;
            string value = expose ? item.Value.Value ?? item.Value.Mode.ToString() : "<redacted>";
            return item.Key + " | " + definition.Kind + " | " + item.Source + " | " + item.Timing + " | " + item.Value.Mode + " | " + value;
        }).ToArray();
    }

    private static async Task<double> ProbeAdvancedDelayAsync(CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp(); await Task.Delay(200, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
}
