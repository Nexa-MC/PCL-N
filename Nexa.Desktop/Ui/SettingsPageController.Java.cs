using Nexa.Services.Minecraft.Java;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId ScanJava = XsrSemanticId.Parse("ui.settings.java.scan");
    private static readonly XsrSemanticId ChooseJava = XsrSemanticId.Parse("ui.settings.java.choose");
    private static readonly XsrSemanticId AddJava = XsrSemanticId.Parse("ui.settings.java.add");
    private Task<string?>? _javaPick;
    private Task<XsrResult>? _javaManagementWrite;
    private CancellationTokenSource? _javaManagementStop;
    private long _javaPickRevision;
    private long _javaRegistryGeneration;
    private bool JavaManagementBusy => _javaPick is not null || _javaManagementWrite is not null;
    private Task<XsrResult<JavaRuntimeInventorySnapshot>>? _javaInventoryRead;
    private CancellationTokenSource? _javaInventoryStop;
    private JavaRuntimeInventorySnapshot? _javaInventory;
    private XsrUiEntityId _javaInventoryGroup;
    private bool _javaInventoryRequested, _javaInventoryFailed;
    private readonly Dictionary<XsrUiEntityId, JavaRuntimeCandidate> _javaChoices = [];
    private static bool IsJavaInventoryIntent(XsrSemanticId command) => command == ScanJava || command == ChooseJava || command == AddJava;
    private bool IsJavaInventoryPage => _instanceDirectory is null ? _selected == "java" : _selected == "game";

    private void BuildJavaInventory(XsrUiEntityId parent = default)
    {
        if (!IsJavaInventoryPage || !_queries.TryResolve(JavaRuntimeInventoryContract.Query, out _)) return;
        string? focus = _shell.Tree.IsAlive(_shell.Renderer.Focused) ? _shell.Tree.Name(_shell.Renderer.Focused) : null;
        if (!_shell.Tree.IsAlive(_javaInventoryGroup)) _javaInventoryGroup = Stack(parent.IsAssigned ? parent : _sections, "SettingsJavaInventory", XsrUiOrientation.Vertical, 10);
        var group = _javaInventoryGroup;
        _shell.Tree.SetComponent(group, new XsrUiSemantic(XsrUiSemanticRole.RadioGroup, "Java 运行时"));
        _shell.Tree.SetComponent(group, new XsrUiSelectionGroup(isSelectionRequired: false));
        foreach (var child in _shell.Tree.Children(group).ToArray())
        {
            _shell.Tree.Walk(child, entity => { _managementActions.Remove(entity); _contentActions.Remove(entity); return true; });
            _shell.Tree.Destroy(child);
        }
        _javaChoices.Clear();
        var header = Stack(group, "SettingsJavaInventory.Header", XsrUiOrientation.Horizontal, 16);
        var label = Text(header, "已安装 Java", _instanceDirectory is null ? 18 : 14, Ink, height: 28, weight: 600);
        _shell.Tree.GetComponent<XsrUiElement>(label)!.Weight = 1;
        if (_instanceDirectory is null && PickRemediationJava is not null && _commands.TryResolve(JavaRuntimeInventoryContract.Manage, out _))
        {
            var add = ActionButton(header, "SettingsJavaAdd", "添加 Java", AddJava, 84);
            _shell.Tree.GetComponent<XsrUiInput>(add)!.Enabled = !JavaManagementBusy && _javaInventoryRead is null && _javaInventory is not null;
        }
        var scan = RefreshIcon(header, "SettingsJavaScan", ScanJava);
        _shell.Tree.GetComponent<XsrUiInput>(scan)!.Enabled = _javaInventoryRead is null && !JavaManagementBusy;
        if (focus is "SettingsJavaScan" or "SettingsJavaAdd") _shell.Tree.Walk(header, entity =>
        { if (_shell.Tree.Name(entity) == focus) _shell.Renderer.Focus(entity, showIndicator: false); return true; });
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
            var choose = RadioOption(row, "SettingsJavaChoose." + index++, "使用", ChooseJava, 64);
            _javaChoices[choose] = candidate;
            BuildJavaManagement(row, candidate.Installation.JavaExecutablePath, candidate.IsEnabled);
        }
        foreach (var entry in _javaInventory.Registrations.Where(entry => entry.Custom && !_javaInventory.Runtimes.Any(candidate =>
            Nexa.Core.PathIdentity.Comparer.Equals(entry.Executable, candidate.Installation.JavaExecutablePath))))
        {
            var row = Stack(group, "SettingsJavaMissing", XsrUiOrientation.Horizontal, 12);
            var labels = Stack(row, "SettingsJavaMissing.Label", XsrUiOrientation.Vertical, 3);
            _shell.Tree.GetComponent<XsrUiElement>(labels)!.Weight = 1;
            Text(labels, "未找到可用的 Java", 13, Muted, 24);
            DesktopLiteralText.Preserve(_shell.Tree, Text(labels, entry.Executable, 11, Muted, 32));
            BuildJavaManagement(row, entry.Executable, entry.Enabled);
        }
        UpdateJavaChoices();
        if (focus is not null) _shell.Tree.Walk(group, entity =>
        { if (_shell.Tree.Name(entity) == focus) _shell.Renderer.Focus(entity, showIndicator: false); return true; });
    }

    private void BuildJavaManagement(XsrUiEntityId row, string executable, bool enabled)
    {
        if (_instanceDirectory is not null || _javaInventory is null || !_commands.TryResolve(JavaRuntimeInventoryContract.Manage, out _)) return;
        BuildBinaryChoice(row, "SettingsJavaEnabled." + executable, "允许使用 Java", enabled,
            value => ManageJava(executable, value ? JavaRuntimeManagementAction.Enable : JavaRuntimeManagementAction.Disable));
        if (_javaInventory.Registrations.Any(entry => entry.Custom && Nexa.Core.PathIdentity.Comparer.Equals(entry.Executable, executable)))
        {
            var remove = ActionButton(row, "SettingsJavaRemove." + executable, "移除登记", ManagementAction, 84);
            RegisterContentAction(remove, () =>
            {
                long revision = _javaInventory.RegistryRevision;
                long generation = _javaRegistryGeneration;
                _feedback.ShowDialog("java.registry.remove", "移除 Java 登记", "移除此 Java 的手动登记，保留磁盘上的文件。\n" + executable,
                    "移除", "取消", accepted =>
                    {
                        if (accepted && generation == _javaRegistryGeneration && IsJavaInventoryPage && _instanceDirectory is null && _javaInventory?.RegistryRevision == revision)
                            ManageJava(executable, JavaRuntimeManagementAction.Remove);
                    });
            });
        }
        _shell.Tree.Walk(row, entity =>
        {
            if (_shell.Tree.GetComponent<XsrUiInput>(entity) is { } input) input.Enabled = !JavaManagementBusy;
            return true;
        });
    }

    private void ManageJava(string executable, JavaRuntimeManagementAction action, long? expectedRevision = null)
    {
        if (_instanceDirectory is not null || !IsJavaInventoryPage || JavaManagementBusy || _javaInventory is null
            || !_commands.TryResolve(JavaRuntimeInventoryContract.Manage, out var route)) return;
        _javaManagementStop ??= new();
        _javaManagementWrite = _commands.Dispatch(route, new JavaRuntimeManageCommand(executable, action,
            expectedRevision ?? _javaInventory.RegistryRevision), cancellationToken: _javaManagementStop.Token).Completion;
        ObserveTransfer(_javaManagementWrite); BuildJavaInventory();
    }

    private async Task<string?> PickRegisteredJavaAsync(CancellationToken token)
    {
        string? path = await PickRemediationJava!().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return path;
    }

    private void HandleJavaInventory(XsrSemanticId command, XsrUiEntityId source)
    {
        if (!IsJavaInventoryPage || !_shell.Tree.IsAlive(source)) return;
        if (command == AddJava && _instanceDirectory is null && _shell.Tree.Name(source) == "SettingsJavaAdd"
            && !JavaManagementBusy && _javaInventoryRead is null && _javaInventory is not null && PickRemediationJava is not null)
        {
            _javaManagementStop?.Dispose(); _javaManagementStop = new(); _javaPickRevision = _javaInventory.RegistryRevision;
            _javaPick = PickRegisteredJavaAsync(_javaManagementStop.Token); ObserveTransfer(_javaPick); BuildJavaInventory(); return;
        }
        if (JavaManagementBusy) return;
        if (command == ScanJava && _shell.Tree.Name(source) == "SettingsJavaScan" && _javaInventoryRead is null)
        {
            CancelJavaInventory(); StartJavaInventory(refresh: true); BuildJavaInventory(); return;
        }
        if (command == ChooseJava && _javaChoices.TryGetValue(source, out var candidate)
            && _javaInventory is not null && _javaInventoryRead is null && candidate.IsAvailable && candidate.IsEnabled && _writing is null
            && _commands.TryResolve(SettingsPolicyContract.SetCommand, out var set))
            _writing = SaveAsync(set, new("java.runtime", _instanceDirectory is null ? SettingsLayer.Global : SettingsLayer.Instance,
                new(SettingsOverrideMode.Custom, candidate.Installation.JavaExecutablePath), _instance));
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
        if (!IsJavaInventoryPage) { CancelJavaInventory(); return; }
        if (_javaPick is { IsCompleted: true } picking)
        {
            _javaPick = null;
            if (picking.IsCompletedSuccessfully && picking.Result is { } path) ManageJava(path, JavaRuntimeManagementAction.Add, _javaPickRevision);
            else if (!picking.IsCanceled && !picking.IsCompletedSuccessfully) _feedback.Error("无法选择 Java，请重试。");
            BuildJavaInventory();
        }
        if (_javaManagementWrite is { IsCompleted: true } writing)
        {
            _javaManagementWrite = null;
            if (!PendingQuery.Succeeded(writing)) _feedback.Error(writing.IsCompletedSuccessfully ? writing.Result.Error?.Message ?? "Java 管理未完成。" : "Java 管理未完成。");
            CancelJavaInventory(); StartJavaInventory(refresh: true); BuildJavaInventory();
        }
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
            _shell.Tree.GetComponent<XsrUiSelection>(entity)!.IsSelected = label == "已选";
            if (text.Content != label) { text.Content = label; _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint); }
            _shell.Tree.GetComponent<XsrUiInput>(entity)!.Enabled = available && _writing is null && _javaInventoryRead is null && !JavaManagementBusy;
        }
    }

    private void CancelJavaInventory()
    {
        _javaRegistryGeneration++;
        _javaManagementStop?.Cancel(); _javaManagementStop?.Dispose(); _javaManagementStop = null;
        _javaPick = null; _javaManagementWrite = null;
        _javaInventoryStop?.Cancel(); _javaInventoryStop?.Dispose(); _javaInventoryStop = null;
        _javaInventoryRead = null; _javaInventory = null; _javaInventoryRequested = _javaInventoryFailed = false;
        _javaChoices.Clear();
        int pending = _pending.Count;
        for (int i = 0; i < pending && _pending.TryDequeue(out var intent); i++)
            if (!IsJavaInventoryIntent(intent.Command)) _pending.Enqueue(intent);
    }
}
