using Nexa.Services.Minecraft.Java;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId ScanJava = XsrSemanticId.Parse("ui.settings.java.scan");
    private static readonly XsrSemanticId ChooseJava = XsrSemanticId.Parse("ui.settings.java.choose");
    private Task<XsrResult<JavaRuntimeInventorySnapshot>>? _javaInventoryRead;
    private CancellationTokenSource? _javaInventoryStop;
    private JavaRuntimeInventorySnapshot? _javaInventory;
    private XsrUiEntityId _javaInventoryGroup;
    private bool _javaInventoryRequested, _javaInventoryFailed;
    private readonly Dictionary<XsrUiEntityId, JavaRuntimeCandidate> _javaChoices = [];
    private static bool IsJavaInventoryIntent(XsrSemanticId command) => command == ScanJava || command == ChooseJava;

    private void BuildJavaInventory()
    {
        if (_instanceDirectory is not null || _selected != "java" || !_queries.TryResolve(JavaRuntimeInventoryContract.Query, out _)) return;
        bool restoreScanFocus = _shell.Tree.IsAlive(_shell.Renderer.Focused) && _shell.Tree.Name(_shell.Renderer.Focused) == "SettingsJavaScan";
        if (!_shell.Tree.IsAlive(_javaInventoryGroup)) _javaInventoryGroup = Stack(_sections, "SettingsJavaInventory", XsrUiOrientation.Vertical, 10);
        var group = _javaInventoryGroup;
        foreach (var child in _shell.Tree.Children(group).ToArray()) _shell.Tree.Destroy(child);
        _javaChoices.Clear();
        var header = Stack(group, "SettingsJavaInventory.Header", XsrUiOrientation.Horizontal, 16);
        var label = Text(header, "已安装 Java", 18, Ink, height: 28, weight: 600);
        _shell.Tree.GetComponent<XsrUiElement>(label)!.Weight = 1;
        var scan = ActionButton(header, "SettingsJavaScan", "重新扫描", ScanJava, 84);
        _shell.Tree.GetComponent<XsrUiInput>(scan)!.Enabled = _javaInventoryRead is null;
        if (restoreScanFocus) _shell.Renderer.Focus(scan, showIndicator: false);
        var divider = Element(group, "SettingsGroupDivider", XsrUiSemanticRole.None, null, height: 1); Style(divider, Line, Muted, 0);
        if (_javaInventory is null)
        {
            Text(group, _javaInventoryFailed ? "无法扫描 Java，请重试。" : "正在扫描 Java…", 12, Muted, height: 24);
            return;
        }
        if (_javaInventory.Runtimes.Count == 0) Text(group, "未找到可用的 Java。", 12, Muted, height: 24);
        int index = 0;
        foreach (var candidate in _javaInventory.Runtimes)
        {
            var runtime = candidate.Installation;
            var row = Stack(group, "SettingsJavaRuntime." + index, XsrUiOrientation.Horizontal, 16);
            var labels = Stack(row, "SettingsJavaRuntime.Label." + index, XsrUiOrientation.Vertical, 4);
            _shell.Tree.GetComponent<XsrUiElement>(labels)!.Weight = 1;
            string brand = runtime.Brand switch
            {
                JavaBrand.EclipseTemurin => "Temurin",
                JavaBrand.IbmSemeru => "IBM Semeru",
                JavaBrand.OpenJdk => "OpenJDK",
                JavaBrand.GraalVmCommunity => "GraalVM",
                JavaBrand.JetBrains => "JetBrains",
                JavaBrand.TencentKona => "Tencent Kona",
                JavaBrand.Unknown => _shell.Renderer.LocalizeText("未知发行方"),
                _ => runtime.Brand.ToString()
            };
            string architecture = runtime.Architecture switch
            { JavaArchitecture.X86 => "x86", JavaArchitecture.X64 => "x64", JavaArchitecture.Arm => "ARM", JavaArchitecture.Arm64 => "ARM64", _ => _shell.Renderer.LocalizeText("未知架构") };
            var name = Text(labels, $"Java {runtime.Version} · {brand} · {architecture}", 14, Ink, height: 22, weight: 500);
            DesktopLiteralText.Preserve(_shell.Tree, name);
            var path = Text(labels, runtime.JavaExecutablePath, 11, Muted, height: 32);
            DesktopLiteralText.Preserve(_shell.Tree, path);
            _shell.Tree.GetComponent<XsrUiVisualStyle>(path)!.WrapText = true;
            _shell.Tree.GetComponent<XsrUiText>(path)!.MaxLines = 2;
            var choose = ActionButton(row, "SettingsJavaChoose." + index++, "使用", ChooseJava, 64);
            _javaChoices[choose] = candidate;
        }
        UpdateJavaChoices();
    }

    private void HandleJavaInventory(XsrSemanticId command, XsrUiEntityId source)
    {
        if (_instanceDirectory is not null || _selected != "java" || !_shell.Tree.IsAlive(source)) return;
        if (command == ScanJava && _shell.Tree.Name(source) == "SettingsJavaScan" && _javaInventoryRead is null)
        {
            CancelJavaInventory(); StartJavaInventory(refresh: true); BuildJavaInventory(); return;
        }
        if (command == ChooseJava && _javaChoices.TryGetValue(source, out var candidate)
            && _javaInventory is not null && _javaInventoryRead is null && candidate.IsAvailable && candidate.IsEnabled && _writing is null
            && _commands.TryResolve(SettingsPolicyContract.SetCommand, out var set))
            _writing = SaveAsync(set, new("java.runtime", SettingsLayer.Global, new(SettingsOverrideMode.Custom, candidate.Installation.JavaExecutablePath)));
    }

    private void StartJavaInventory(bool refresh)
    {
        _javaInventoryRequested = true;
        if (!_queries.TryResolve(JavaRuntimeInventoryContract.Query, out var read)) return;
        _javaInventoryStop = new();
        _javaInventoryRead = _queries.QueryAsync<JavaRuntimeInventoryQuery, JavaRuntimeInventorySnapshot>(read,
            new(refresh), cancellationToken: _javaInventoryStop.Token).AsTask();
        ObserveTransfer(_javaInventoryRead);
    }

    private void UpdateJavaInventory()
    {
        if (_instanceDirectory is not null || _selected != "java") { CancelJavaInventory(); return; }
        if (!_javaInventoryRequested) { StartJavaInventory(refresh: false); BuildJavaInventory(); }
        if (_javaInventoryRead is { IsCompleted: true } reading)
        {
            _javaInventoryRead = null;
            _javaInventoryStop?.Dispose(); _javaInventoryStop = null;
            _javaInventoryFailed = !PendingQuery.Succeeded(reading);
            _javaInventory = _javaInventoryFailed ? null : reading.Result.Value;
            BuildJavaInventory();
        }
        UpdateJavaChoices();
    }

    private void UpdateJavaChoices()
    {
        string? selected = _values?.Values.FirstOrDefault(value => value.Key == "java.runtime")?.Value.Value;
        foreach (var (entity, candidate) in _javaChoices)
        {
            bool available = candidate.IsEnabled && candidate.IsAvailable;
            string label = !candidate.IsAvailable ? "不可用" : !candidate.IsEnabled ? "已禁用" : string.Equals(candidate.Installation.JavaExecutablePath, selected,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? "已选" : "使用";
            var text = _shell.Tree.GetComponent<XsrUiText>(entity)!;
            if (text.Content != label) { text.Content = label; _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint); }
            _shell.Tree.GetComponent<XsrUiInput>(entity)!.Enabled = available && _writing is null && _javaInventoryRead is null;
        }
    }

    private void CancelJavaInventory()
    {
        _javaInventoryStop?.Cancel(); _javaInventoryStop?.Dispose(); _javaInventoryStop = null;
        _javaInventoryRead = null; _javaInventory = null; _javaInventoryRequested = _javaInventoryFailed = false;
        _javaChoices.Clear();
        int pending = _pending.Count;
        for (int i = 0; i < pending && _pending.TryDequeue(out var intent); i++)
            if (!IsJavaInventoryIntent(intent.Command)) _pending.Enqueue(intent);
    }
}
