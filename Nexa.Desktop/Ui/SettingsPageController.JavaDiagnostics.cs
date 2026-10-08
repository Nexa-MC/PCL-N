using Nexa.Core;
using Nexa.Services.Minecraft.Java;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private readonly Dictionary<XsrUiEntityId, Action> _javaDiagnosticActions = [];
    private Task<XsrResult<JavaRuntimeDiagnosticsSnapshot>>? _javaDiagnosticRead;
    private CancellationTokenSource? _javaDiagnosticStop;
    private JavaRuntimeDiagnosticsSnapshot? _javaDiagnosticSnapshot;
    private XsrUiEntityId _javaDiagnosticRoot, _javaDiagnosticGroup;
    private string? _javaDiagnosticExecutable;
    private long _javaDiagnosticRegistry = -1;
    private int _javaDiagnosticPage;
    internal Func<string, Task>? CopyJavaDiagnosticsTextAsync { get; set; }

    private void CancelJavaDiagnostics()
    {
        _javaDiagnosticStop?.Cancel(); _javaDiagnosticStop?.Dispose(); _javaDiagnosticStop = null;
        _javaDiagnosticRead = null; _javaDiagnosticSnapshot = null;
        foreach (var entity in _javaDiagnosticActions.Keys) _managementActions.Remove(entity);
        _javaDiagnosticExecutable = null; _javaDiagnosticRegistry = -1; _javaDiagnosticActions.Clear();
        if (_shell.Tree.IsAlive(_javaDiagnosticRoot)) _shell.Tree.Destroy(_javaDiagnosticRoot);
        _javaDiagnosticRoot = _javaDiagnosticGroup = default;
    }

    private void UpdateJavaDiagnostics()
    {
        if (!_visible || !IsJavaInventoryPage) { CancelJavaDiagnostics(); return; }
        if (_javaInventory is null || _javaDiagnosticRegistry != _javaInventory.RegistryRevision
            || _javaDiagnosticExecutable is not null && !_javaInventory.Runtimes.Any(item => item.IsAvailable && PathIdentity.Comparer.Equals(item.Installation.JavaExecutablePath, _javaDiagnosticExecutable)))
        { CancelJavaDiagnostics(); BuildJavaDiagnostics(); return; }
        if (_javaDiagnosticRead is not { IsCompleted: true } completed) return;
        _javaDiagnosticRead = null;
        if (PendingQuery.Succeeded(completed)) _javaDiagnosticSnapshot = completed.Result.Value;
        else if (!completed.IsCanceled) _javaDiagnosticSnapshot = new(JavaRuntimeDiagnosticStatus.Failed, _javaDiagnosticExecutable ?? "", "", null, [], "", 0);
        BuildJavaDiagnostics();
    }

    private void BuildJavaDiagnostics()
    {
        if (!IsJavaInventoryPage || !_shell.Tree.IsAlive(_javaInventoryGroup) || _javaInventory is null
            || !_queries.TryResolve(JavaRuntimeDiagnosticsContract.Properties, out _)) return;
        if (!_shell.Tree.IsAlive(_javaDiagnosticRoot) || !_shell.Tree.IsAlive(_javaDiagnosticGroup)
            || _shell.Tree.Parent(_javaDiagnosticRoot) != _javaInventoryGroup)
        {
            if (_shell.Tree.IsAlive(_javaDiagnosticRoot)) _shell.Tree.Destroy(_javaDiagnosticRoot);
            _javaDiagnosticRoot = Stack(_javaInventoryGroup, "JavaDiagnostics.Root", XsrUiOrientation.Vertical, 0);
            _javaDiagnosticGroup = FormGroup(_javaDiagnosticRoot, "JavaDiagnostics", "Java 运行时诊断");
        }
        foreach (var child in _shell.Tree.Children(_javaDiagnosticGroup).ToArray()) _shell.Tree.Destroy(child);
        foreach (var key in _javaDiagnosticActions.Keys) _managementActions.Remove(key);
        _javaDiagnosticActions.Clear();
        _javaDiagnosticRegistry = _javaInventory.RegistryRevision;
        if (_javaDiagnosticExecutable is null) _javaDiagnosticExecutable = _javaInventory.Runtimes.FirstOrDefault(item => item.IsAvailable)?.Installation.JavaExecutablePath;
        Text(_javaDiagnosticGroup, "只执行版本、属性和模块查询；原始预览先脱敏，不修改 Java 或启动游戏。", 11, Muted, 34);
        int index = 0;
        foreach (var candidate in _javaInventory.Runtimes.Where(item => item.IsAvailable).Take(32))
        {
            string executable = candidate.Installation.JavaExecutablePath;
            var select = ActionButton(_javaDiagnosticGroup, "JavaDiagnostics.Select." + index++, executable, ManagementAction, 360);
            DesktopLiteralText.Preserve(_shell.Tree, select);
            _shell.Tree.SetComponent(select, new XsrUiSelection { IsSelected = PathIdentity.Comparer.Equals(_javaDiagnosticExecutable, executable) });
            Bind(select, () =>
            {
                _javaDiagnosticStop?.Cancel(); _javaDiagnosticStop?.Dispose(); _javaDiagnosticStop = null;
                _javaDiagnosticRead = null; _javaDiagnosticSnapshot = null; _javaDiagnosticExecutable = executable; _javaDiagnosticPage = 0; BuildJavaDiagnostics();
            });
        }
        if (_javaDiagnosticExecutable is null) { Text(_javaDiagnosticGroup, "没有已扫描的可用 Java，不能执行诊断。", 12, Muted, 28); return; }
        var actions = Stack(_javaDiagnosticGroup, "JavaDiagnostics.Actions", XsrUiOrientation.Horizontal, 8);
        Add(actions, "JavaDiagnostics.Properties", "读取运行时属性", () => StartJavaDiagnostic(false));
        Add(actions, "JavaDiagnostics.Modules", "列出 Java 模块", () => StartJavaDiagnostic(true));
        if (_javaDiagnosticRead is not null) { Text(_javaDiagnosticGroup, "正在读取 Java 诊断…", 12, Muted, 28); return; }
        if (_javaDiagnosticSnapshot is not { } snapshot) return;
        Text(_javaDiagnosticGroup, JavaDiagnosticCaption(snapshot.Status), 12, snapshot.Status == JavaRuntimeDiagnosticStatus.Available ? Blue : Muted, 28);
        if (snapshot.Facts is { } facts)
        {
            ManagementFactIn(_javaDiagnosticGroup, "实际版本", facts.Version, true);
            ManagementFactIn(_javaDiagnosticGroup, "实际发行方", facts.Vendor, true);
            ManagementFactIn(_javaDiagnosticGroup, "实际架构", facts.Architecture, true);
            ManagementFactIn(_javaDiagnosticGroup, "虚拟机", facts.VirtualMachine, true);
            ManagementFactIn(_javaDiagnosticGroup, "虚拟机版本", facts.VirtualMachineVersion, true);
            Text(_javaDiagnosticGroup, facts.MatchesInventory ? "与扫描结果一致。" : "与扫描结果不一致，请重新扫描。", 11, Muted, 28);
        }
        if (snapshot.Fingerprint.Length > 0) ManagementFactIn(_javaDiagnosticGroup, "可执行文件 SHA-256", snapshot.Fingerprint, true);
        string[] raw = snapshot.RedactedRaw.Split('\n');
        int pages = Math.Max(1, (Math.Max(raw.Length, snapshot.Modules.Count) + 31) / 32);
        _javaDiagnosticPage = Math.Clamp(_javaDiagnosticPage, 0, pages - 1);
        foreach (var module in snapshot.Modules.Skip(_javaDiagnosticPage * 32).Take(32))
            DesktopLiteralText.Preserve(_shell.Tree, Text(_javaDiagnosticGroup, module.Name + (module.Version.Length == 0 ? "" : "@" + module.Version), 11, Ink, 24));
        if (snapshot.RedactedRaw.Length > 0)
        {
            Text(_javaDiagnosticGroup, "脱敏原始预览", 13, Ink, 28);
            foreach (string line in raw.Skip(_javaDiagnosticPage * 32).Take(32))
                DesktopLiteralText.Preserve(_shell.Tree, Text(_javaDiagnosticGroup, line.Length <= 1024 ? line : line[..1024], 11, Muted, 24));
            if (CopyJavaDiagnosticsTextAsync is not null) Add(actions, "JavaDiagnostics.Copy", "复制脱敏预览", () =>
            {
                var copy = CopyJavaDiagnosticsTextAsync(snapshot.RedactedRaw); ObserveTransfer(copy);
            });
        }
        if (pages > 1) Add(actions, "JavaDiagnostics.Next", "下一段", () => { _javaDiagnosticPage = (_javaDiagnosticPage + 1) % pages; BuildJavaDiagnostics(); });
        _shell.Tree.MarkDirty(_javaDiagnosticGroup, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);

        void Add(XsrUiEntityId parent, string name, string label, Action action) => Bind(ActionButton(parent, name, label, ManagementAction, 128), action);
        void Bind(XsrUiEntityId entity, Action action)
        {
            _javaDiagnosticActions[entity] = action;
            _managementActions[entity] = () =>
            {
                if (_visible && IsJavaInventoryPage && _shell.Tree.IsAlive(entity) && _javaInventory?.RegistryRevision == _javaDiagnosticRegistry
                    && _shell.Tree.GetComponent<XsrUiInput>(entity)?.Enabled == true) action();
            };
            _shell.Tree.GetComponent<XsrUiInput>(entity)!.Enabled = _javaDiagnosticRead is null;
        }
    }

    private void StartJavaDiagnostic(bool modules)
    {
        if (_javaDiagnosticRead is not null || _javaDiagnosticExecutable is null || _javaInventory is null) return;
        _javaDiagnosticStop?.Dispose(); _javaDiagnosticStop = new(); _javaDiagnosticSnapshot = null; _javaDiagnosticPage = 0;
        if (modules && _queries.TryResolve(JavaRuntimeDiagnosticsContract.Modules, out var moduleRoute))
            _javaDiagnosticRead = _queries.QueryAsync<JavaRuntimeModulesQuery, JavaRuntimeDiagnosticsSnapshot>(moduleRoute,
                new(_javaDiagnosticExecutable, _javaDiagnosticRegistry), cancellationToken: _javaDiagnosticStop.Token).AsTask();
        else if (!modules && _queries.TryResolve(JavaRuntimeDiagnosticsContract.Properties, out var propertiesRoute))
            _javaDiagnosticRead = _queries.QueryAsync<JavaRuntimePropertiesQuery, JavaRuntimeDiagnosticsSnapshot>(propertiesRoute,
                new(_javaDiagnosticExecutable, _javaDiagnosticRegistry), cancellationToken: _javaDiagnosticStop.Token).AsTask();
        if (_javaDiagnosticRead is not null) ObserveTransfer(_javaDiagnosticRead);
        BuildJavaDiagnostics();
    }

    private static string JavaDiagnosticCaption(JavaRuntimeDiagnosticStatus status) => status switch
    {
        JavaRuntimeDiagnosticStatus.Available => "Java 诊断已完成。",
        JavaRuntimeDiagnosticStatus.PlatformUnsupported => "Java 8 不支持模块列表。",
        JavaRuntimeDiagnosticStatus.Stale => "Java 登记已变化，请重新扫描。",
        JavaRuntimeDiagnosticStatus.IdentityChanged => "Java 可执行文件已变化，诊断结果未采用。",
        JavaRuntimeDiagnosticStatus.TimedOut => "Java 诊断超时，已结束探测进程。",
        JavaRuntimeDiagnosticStatus.OutputLimit => "Java 输出超过预算，已结束探测进程。",
        JavaRuntimeDiagnosticStatus.Malformed => "Java 返回的事实不完整，不能确认诊断结果。",
        JavaRuntimeDiagnosticStatus.Rejected => "此 Java 未通过扫描列表准入。",
        _ => "Java 诊断未完成，请检查可执行文件。",
    };
}
