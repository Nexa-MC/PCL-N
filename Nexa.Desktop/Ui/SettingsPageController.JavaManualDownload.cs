using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Nexa.Services.Minecraft.Java;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private XsrQueryRouter? _javaManualQueries;
    private XsrCommandRouter? _javaManualCommands;
    private Action<Uri>? _javaManualOpenLicense;
    private readonly Dictionary<XsrUiEntityId, Action> _javaManualActions = [];
    private readonly ConcurrentQueue<(long Generation, Guid Preview, bool Accepted)> _javaManualDecisions = new();
    private CancellationTokenSource? _javaManualStop;
    private CancellationTokenSource? _javaManualInstallStop;
    private Task<XsrResult<JavaManualPreview>>? _javaManualRead;
    private Task<XsrResult>? _javaManualInstall;
    private Task<XsrResult<JavaManualReceipt>>? _javaManualStatusRead;
    private JavaManualPreview? _javaManualPreview;
    private JavaManualReceipt? _javaManualReceipt;
    private XsrUiEntityId _javaManualRoot, _javaManualGroup;
    private long _javaManualGeneration, _javaManualNextPoll, _javaManualReadGeneration, _javaManualStatusGeneration;
    private Guid _javaManualStatusPreview;
    private int _javaManualMajor = 21;
    private string? _javaManualError;
    private Guid _javaManualDialog;
    private bool _javaManualStarted;
    private bool JavaManualVisible => _visible && !_disposed && IsJavaInventoryPage;
    private bool JavaManualBusy => _javaManualRead is not null || _javaManualInstall is not null || _javaManualDialog != Guid.Empty;

    internal void ConfigureJavaManualDownload(XsrQueryRouter queries, XsrCommandRouter commands, Action<Uri>? openLicense = null)
    { _javaManualQueries = queries; _javaManualCommands = commands; _javaManualOpenLicense = openLicense; }

    private void CancelJavaManualDownload()
    {
        _javaManualGeneration++; _javaManualStop?.Cancel(); _javaManualStop?.Dispose(); _javaManualStop = null;
        _javaManualInstallStop?.Cancel(); _javaManualInstallStop?.Dispose(); _javaManualInstallStop = null;
        if (_javaManualDialog != Guid.Empty) _feedback.DismissDialog(_javaManualDialog); _javaManualDialog = default;
        _javaManualRead = null; _javaManualInstall = null; _javaManualStatusRead = null; _javaManualPreview = null; _javaManualReceipt = null; _javaManualError = null; _javaManualStarted = false;
        _javaManualReadGeneration = _javaManualStatusGeneration = _javaManualNextPoll = 0; _javaManualStatusPreview = default;
        foreach (var key in _javaManualActions.Keys) _managementActions.Remove(key); _javaManualActions.Clear();
        while (_javaManualDecisions.TryDequeue(out _)) { }
        if (_shell.Tree.IsAlive(_javaManualRoot)) _shell.Tree.Destroy(_javaManualRoot); _javaManualRoot = _javaManualGroup = default;
    }

    private bool RetireJavaManualObservations()
    {
        // An actual install retains its linked cancellation ownership until completion.
        if (_javaManualInstall is not null || _javaManualDialog != Guid.Empty) return false;
        _javaManualGeneration++; _javaManualStop?.Cancel(); _javaManualStop?.Dispose(); _javaManualStop = null;
        _javaManualRead = null; _javaManualStatusRead = null;
        _javaManualReadGeneration = _javaManualStatusGeneration = _javaManualNextPoll = 0; _javaManualStatusPreview = default;
        _javaManualPreview = null; _javaManualReceipt = null; _javaManualError = null; _javaManualStarted = false;
        return true;
    }

    private void UpdateJavaManualDownload()
    {
        if (!JavaManualVisible) { if (_javaManualStop is not null || _javaManualRoot.IsAssigned) CancelJavaManualDownload(); return; }
        bool changed = false;
        while (_javaManualDecisions.TryDequeue(out var decision))
        {
            if (decision.Generation != _javaManualGeneration || _javaManualPreview?.Id != decision.Preview) continue;
            _javaManualDialog = default; changed = true;
            if (decision.Accepted && _javaManualCommands is not null && _javaManualPreview.ExpiresAt > DateTimeOffset.UtcNow
                && _javaManualCommands.TryResolve(JavaManualDownloadContract.Install, out var install))
            {
                _javaManualStop ??= new();
                _javaManualInstallStop?.Dispose(); _javaManualInstallStop = CancellationTokenSource.CreateLinkedTokenSource(_javaManualStop.Token);
                _javaManualInstall = _javaManualCommands.Dispatch(install, new JavaManualInstallCommand(decision.Preview, true), cancellationToken: _javaManualInstallStop.Token).Completion;
                ObserveTransfer(_javaManualInstall); _javaManualNextPoll = 0; _javaManualStarted = true;
            }
        }
        if (_javaManualRead is { IsCompleted: true } reading)
        {
            long generation = _javaManualReadGeneration;
            _javaManualRead = null; changed = true;
            _javaManualReadGeneration = 0;
            if (generation == _javaManualGeneration)
            {
                if (PendingQuery.Succeeded(reading) && reading.Result.Value is { } captured && captured.Major == _javaManualMajor) _javaManualPreview = captured;
                else if (!reading.IsCanceled) _javaManualError = "无法读取有效的 Java 下载预览。";
            }
        }
        if (_javaManualInstall is { IsCompleted: true } installing)
        {
            _javaManualInstall = null; changed = true; _javaManualNextPoll = 0;
            _javaManualInstallStop?.Dispose(); _javaManualInstallStop = null;
            if (!installing.IsCanceled && !PendingQuery.Succeeded(installing) && _javaManualReceipt is null) _javaManualError = "Java 安装未完成全部验证与登记，请查看实际结果。";
        }
        if (_javaManualStatusRead is { IsCompleted: true } status)
        {
            long generation = _javaManualStatusGeneration; Guid previewId = _javaManualStatusPreview;
            _javaManualStatusRead = null; _javaManualStatusGeneration = 0; _javaManualStatusPreview = default;
            if (generation == _javaManualGeneration && _javaManualPreview?.Id == previewId)
            {
                if (PendingQuery.Succeeded(status) && status.Result.Value is { } captured && captured.PreviewId == previewId)
                { changed |= _javaManualReceipt != captured; _javaManualReceipt = captured; }
                else if (_javaManualInstall is null) { _javaManualStarted = false; _javaManualError = "Java 安装未完成全部验证与登记，请查看实际结果。"; changed = true; }
                _javaManualNextPoll = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
            }
        }
        if (_javaManualStarted && _javaManualPreview is { } preview && _javaManualReceipt?.Status is null or JavaManualInstallStatus.Downloading
            && _javaManualStatusRead is null
            && Stopwatch.GetTimestamp() >= _javaManualNextPoll && _javaManualQueries?.TryResolve(JavaManualDownloadContract.Status, out var route) == true)
        {
            _javaManualStop ??= new();
            _javaManualStatusGeneration = _javaManualGeneration; _javaManualStatusPreview = preview.Id;
            _javaManualStatusRead = _javaManualQueries.QueryAsync<JavaManualStatusQuery, JavaManualReceipt>(route, new(preview.Id), cancellationToken: _javaManualStop.Token).AsTask();
            ObserveTransfer(_javaManualStatusRead);
        }
        if (changed) BuildJavaManualDownload();
    }

    private void BuildJavaManualDownload()
    {
        if (!JavaManualVisible || !_shell.Tree.IsAlive(_javaInventoryGroup) || _javaInventory is null
            || _javaManualQueries?.TryResolve(JavaManualDownloadContract.Preview, out _) != true) return;
        if (!_shell.Tree.IsAlive(_javaManualRoot) || _shell.Tree.Parent(_javaManualRoot) != _javaInventoryGroup)
        { _javaManualRoot = Stack(_javaInventoryGroup, "JavaManual.Root", XsrUiOrientation.Vertical, 0); _javaManualGroup = FormGroup(_javaManualRoot, "JavaManual", "手动下载 Java"); }
        foreach (var child in _shell.Tree.Children(_javaManualGroup).ToArray()) _shell.Tree.Destroy(child);
        foreach (var key in _javaManualActions.Keys) _managementActions.Remove(key); _javaManualActions.Clear();
        Text(_javaManualGroup, "来源：Mojang 分发。实际发行方将在安装后探测；安装前必须阅读并确认运行时许可证。", 11, Muted, 40);
        var versions = Stack(_javaManualGroup, "JavaManual.Versions", XsrUiOrientation.Horizontal, 8);
        foreach (int major in new[] { 8, 17, 21, 25 })
        {
            var entity = Add(versions, "JavaManual.Major." + major, "Java " + major.ToString(CultureInfo.InvariantCulture), () =>
            { if (!RetireJavaManualObservations()) return; _javaManualMajor = major; BuildJavaManualDownload(); }, !JavaManualBusy);
            _shell.Tree.SetComponent(entity, new XsrUiSelection { IsSelected = _javaManualMajor == major });
        }
        Add(_javaManualGroup, "JavaManual.Preview", "读取下载预览", () =>
        {
            if (_javaManualQueries is null || _javaInventory is null || !_javaManualQueries.TryResolve(JavaManualDownloadContract.Preview, out var route)) return;
            if (!RetireJavaManualObservations()) return;
            _javaManualStop = new(); _javaManualReadGeneration = _javaManualGeneration;
            _javaManualRead = _javaManualQueries.QueryAsync<JavaManualPreviewQuery, JavaManualPreview>(route, new(_javaManualMajor, _javaInventory.RegistryRevision), cancellationToken: _javaManualStop.Token).AsTask();
            ObserveTransfer(_javaManualRead); BuildJavaManualDownload();
        }, !JavaManualBusy);
        if (_javaManualRead is not null) Text(_javaManualGroup, "正在读取 Java 下载清单…", 12, Muted, 28);
        if (_javaManualError is { } error) Text(_javaManualGroup, error, 12, Muted, 32);
        if (_javaManualPreview is not { } preview) return;
        ManagementFactIn(_javaManualGroup, "清单版本", preview.Version, true); ManagementFactIn(_javaManualGroup, "目标平台", preview.Platform, true);
        ManagementFactIn(_javaManualGroup, "安装目录", preview.TargetDirectory, true);
        ManagementFactIn(_javaManualGroup, "文件数量", preview.Files.ToString(CultureInfo.InvariantCulture), true);
        ManagementFactIn(_javaManualGroup, "下载字节", preview.Bytes.ToString(CultureInfo.InvariantCulture), true);
        ManagementFactIn(_javaManualGroup, "确认清单 SHA-256", preview.PlanFingerprint, true);
        int index = 0;
        foreach (string license in preview.LicenseUrls)
        {
            DesktopLiteralText.Preserve(_shell.Tree, Text(_javaManualGroup, license, 11, Muted, 30));
            Add(_javaManualGroup, "JavaManual.License." + index++, "打开运行时许可证", () => _javaManualOpenLicense?.Invoke(new Uri(license)), _javaManualOpenLicense is not null);
        }
        Add(_javaManualGroup, "JavaManual.Install", "确认许可证并安装", () =>
        {
            if (preview.ExpiresAt <= DateTimeOffset.UtcNow) { _javaManualError = "下载预览已过期，请重新读取。"; BuildJavaManualDownload(); return; }
            long generation = _javaManualGeneration;
            string description = string.Format(CultureInfo.InvariantCulture, _shell.Renderer.LocalizeText("将从 Mojang 分发源下载 {0} 个文件（{1} 字节）到：{2}。\n确认已阅读并接受上述运行时许可证。取消会保留可恢复的下载记录。"), preview.Files, preview.Bytes, preview.TargetDirectory);
            _javaManualDialog = _feedback.ShowDialog("java.manual.license", "确认 Java 运行时许可证", description, "接受并安装", "返回",
                accepted => _javaManualDecisions.Enqueue((generation, preview.Id, accepted)), localizeMessage: false); BuildJavaManualDownload();
        }, !JavaManualBusy && _javaManualReceipt is null && preview.ExpiresAt > DateTimeOffset.UtcNow);
        if (_javaManualInstall is not null) Add(_javaManualGroup, "JavaManual.Cancel", "取消此 Java 下载", () => _javaManualInstallStop?.Cancel());
        Text(_javaManualGroup, "取消仅暂停此次下载；完整性记录将保留，后续启动可恢复。", 11, Muted, 32);
        if (_javaManualReceipt is not { } receipt) return;
        Text(_javaManualGroup, ManualJavaCaption(receipt.Status), 12, receipt.Status == JavaManualInstallStatus.Installed ? Blue : Muted, 30);
        DesktopLiteralText.Preserve(_shell.Tree, Text(_javaManualGroup, $"{receipt.CompletedFiles}/{receipt.TotalFiles} · {receipt.Progress:P0}", 11, Muted, 26));
        if (receipt.Detail.Length > 0) DesktopLiteralText.Preserve(_shell.Tree, Text(_javaManualGroup, receipt.Detail, 11, Muted, 28));
        if (receipt.Executable.Length > 0) ManagementFactIn(_javaManualGroup, "实际可执行文件", receipt.Executable, true);
        if (receipt.ActualVersion.Length > 0) ManagementFactIn(_javaManualGroup, "实际版本", receipt.ActualVersion, true);
        if (receipt.ActualBrand is { } brand) ManagementFactIn(_javaManualGroup, "实际发行方", brand.ToString(), true);
        if (receipt.ActualArchitecture is { } architecture) ManagementFactIn(_javaManualGroup, "实际架构", architecture.ToString(), true);
        if (receipt.Status == JavaManualInstallStatus.Installed) Add(_javaManualGroup, "JavaManual.Refresh", "刷新已安装 Java", () =>
        { CancelJavaInventory(); StartJavaInventory(refresh: true); BuildJavaInventory(); });

        XsrUiEntityId Add(XsrUiEntityId parent, string name, string label, Action action, bool enabled = true)
        {
            var entity = ActionButton(parent, name, label, ManagementAction, 180); _javaManualActions[entity] = action;
            _shell.Tree.GetComponent<XsrUiInput>(entity)!.Enabled = enabled;
            _managementActions[entity] = () => { if (JavaManualVisible && _shell.Tree.IsAlive(entity) && _shell.Tree.GetComponent<XsrUiInput>(entity)?.Enabled == true) action(); };
            return entity;
        }
    }
    private static string ManualJavaCaption(JavaManualInstallStatus status) => status switch
    {
        JavaManualInstallStatus.Downloading => "正在校验并下载 Java…",
        JavaManualInstallStatus.Installed => "Java 已安装、探测并登记。",
        JavaManualInstallStatus.InstalledUnverified => "文件已安装，但实际 Java 探测未通过。",
        JavaManualInstallStatus.InstalledUnregistered => "Java 已安装并探测，但登记已变化；请重新扫描。",
        JavaManualInstallStatus.Canceled => "此 Java 下载已取消，恢复记录已保留。",
        _ => "Java 下载或清单验证失败，请重新预览。",
    };
}
