using Nexa.Services.Minecraft.Management;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private Task<XsrResult<InstanceOfflineReadinessReport>>? _offlineRead;
    private CancellationTokenSource? _offlineStop;
    private InstanceOfflineReadinessReport? _offlineReport;
    private string? _offlineInstance;
    private long _offlineSettingsRevision;
    private string? _offlineError;

    private void BuildOfflineReadiness(InstanceManagementSnapshot snapshot)
    {
        if (_selected is not ("overview" or "recovery") || !_queries.TryResolve(InstanceOfflineReadinessContract.Query, out _)) return;
        if (_offlineInstance != snapshot.InstanceDirectory) { CancelOfflineReadiness(); _offlineInstance = snapshot.InstanceDirectory; _offlineReport = null; _offlineError = null; }
        var group = FormGroup(_sections, "InstanceOfflineReadiness", "离线就绪");
        Text(group, "检查本地版本、库、Native、资源索引/对象及兼容 Java；不下载、不登录，也不替代启动前检查。", 11, Muted, 42);
        if (_offlineRead is null) ManagementButton(group, "检查本地离线依赖", BeginOfflineReadiness, 164);
        else Text(group, "正在检查本地文件…", 12, Muted, 28);
        if (_offlineError is { } error) Text(group, error, 12, Muted, 42);
        if (_offlineReport is not { } report) return;
        ManagementFactIn(group, "检查结果", report.Ready ? "本地离线依赖校验通过" : report.Complete ? "尚未离线就绪" : "检查不完整");
        ManagementFactIn(group, "Java", report.JavaCompatible == true ? "兼容 · " + report.JavaVersion : report.JavaCompatible == false ? "缺失或不兼容" : "尚无法确认");
        ManagementFactIn(group, "已校验文件", report.Artifacts.Count(x => x.State == OfflineArtifactState.Verified).ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var fact in report.Artifacts.Where(x => x.State != OfflineArtifactState.Verified).Take(20))
        {
            string label = fact.State switch { OfflineArtifactState.Missing => "缺失", OfflineArtifactState.Corrupt => "损坏", OfflineArtifactState.PresentUnverified => "缺少完整性证据", _ => "无法检查" };
            var text = Text(group, _shell.Renderer.LocalizeText(label) + " · " + fact.RelativePath + " · " + fact.ReasonCode, 11, Muted, 32);
            DesktopLiteralText.Preserve(_shell.Tree, text);
        }
        if (report.Artifacts.Count(x => x.State != OfflineArtifactState.Verified) > 20) Text(group, "显示前 20 项问题；解决后重新检查。", 11, Muted, 28);
    }

    private void BeginOfflineReadiness()
    {
        if (_instance is not { } instance || _selected is not ("overview" or "recovery") || _offlineRead is not null
            || !_queries.TryResolve(InstanceOfflineReadinessContract.Query, out var route)) return;
        CancelOfflineReadiness(); _offlineInstance = instance; _offlineError = null; _offlineStop = new();
        _offlineSettingsRevision = _store.Read<long>(_revisionId).Value;
        _offlineRead = _queries.QueryAsync<InstanceOfflineReadinessQuery, InstanceOfflineReadinessReport>(route, new(instance), cancellationToken: _offlineStop.Token).AsTask();
        WakeOnPlatformCompletion(_offlineRead); BuildSections();
    }

    private void UpdateOfflineReadiness()
    {
        if (!_visible || _selected is not ("overview" or "recovery") || _offlineInstance is not null && _instance != _offlineInstance)
        { CancelOfflineReadiness(); return; }
        if (_offlineRead is not { IsCompleted: true } completed) return;
        _offlineRead = null;
        if (_store.Read<long>(_revisionId).Value != _offlineSettingsRevision) { _offlineReport = null; _offlineError = "设置在检查期间改变，请重新检查。"; }
        else if (PendingQuery.Succeeded(completed)) { _offlineReport = completed.Result.Value; _offlineError = null; }
        else if (!completed.IsCanceled) _offlineError = "无法完成本地离线检查，请检查目录权限后重试。";
        BuildSections();
    }

    private void CancelOfflineReadiness()
    { _offlineStop?.Cancel(); _offlineStop?.Dispose(); _offlineStop = null; _offlineRead = null; }
}
