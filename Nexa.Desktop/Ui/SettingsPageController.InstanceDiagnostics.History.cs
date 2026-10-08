using System.Globalization;
using Nexa.Services.Logging;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private Task<XsrResult<IReadOnlyList<DurableDiagnosticEntry>>>? _instanceHistoryRead;
    private IReadOnlyList<DurableDiagnosticEntry>? _instanceHistory;
    private CancellationTokenSource? _instanceHistoryStop;
    private string? _instanceHistoryError;
    private int _instanceLaunchHistoryPage, _instanceCrashHistoryPage;

    private void CancelInstanceDiagnosticHistory()
    {
        _instanceHistoryStop?.Cancel(); _instanceHistoryStop?.Dispose(); _instanceHistoryStop = null;
        _instanceHistoryRead = null; _instanceHistory = null; _instanceHistoryError = null;
        _instanceLaunchHistoryPage = _instanceCrashHistoryPage = 0;
        _instanceOperationPage = 0; _instanceOperationView = false;
    }

    private void BuildInstanceDiagnosticHistory()
    {
        if (_instanceHistory is null && _instanceHistoryRead is null && _instanceHistoryError is null)
        {
            if (_queries.TryResolve(DiagnosticWorkspaceContract.InstanceHistory, out var route))
            {
                _instanceHistoryStop = new(); var query = new DurableInstanceDiagnosticHistoryQuery(_instance!); var token = _instanceHistoryStop.Token;
                _instanceHistoryRead = Task.Run(async () => await _queries.QueryAsync<DurableInstanceDiagnosticHistoryQuery, IReadOnlyList<DurableDiagnosticEntry>>(route, query, cancellationToken: token).ConfigureAwait(false), token);
                WakeOnPlatformCompletion(_instanceHistoryRead);
            }
            else _instanceHistoryError = "此环境未提供实例持久诊断历史。";
        }
        var launches = FormGroup(_sections, _instanceOperationView ? "InstanceDiagnostics.OperationHistory" : "InstanceDiagnostics.LaunchHistory", _instanceOperationView ? "操作阶段历史" : "启动历史");
        if (_instanceHistoryError is { } error) Text(launches, error, 12, Muted, 32);
        if (_instanceHistory is null)
        { if (_instanceHistoryRead is not null) Text(launches, "正在读取此实例的持久运行历史…", 12, Muted, 30); return; }
        if (_instanceOperationView) { BuildInstanceOperationHistory(launches); return; }
        var grouped = _instanceHistory.Where(entry => entry.Instance is not null)
            .GroupBy(entry => entry.Instance!.SessionId).Select(group => group.OrderByDescending(entry => entry.Timestamp).First()).OrderByDescending(entry => entry.Timestamp).ToArray();
        BuildInstanceHistoryRows(launches, grouped, crashes: false);
        var crashes = FormGroup(_sections, "InstanceDiagnostics.CrashHistory", "崩溃历史");
        var failures = _instanceHistory.Where(entry => entry.Instance is { } instance && (instance.FailureCode is not null || instance.ExitCode is not null and not 0))
            .GroupBy(entry => entry.Instance!.SessionId).Select(group => group.OrderByDescending(entry => entry.Timestamp).First()).OrderByDescending(entry => entry.Timestamp).ToArray();
        BuildInstanceHistoryRows(crashes, failures, crashes: true);
        Text(crashes, "历史仅包含结构化运行事实；旧的无实例身份记录不会混入此页。", 11, Muted, 34);
    }

    private void BuildInstanceHistoryRows(XsrUiEntityId parent, DurableDiagnosticEntry[] entries, bool crashes)
    {
        int pages = Math.Max(1, (entries.Length + 11) / 12);
        int page = Math.Clamp(crashes ? _instanceCrashHistoryPage : _instanceLaunchHistoryPage, 0, pages - 1);
        if (crashes) _instanceCrashHistoryPage = page; else _instanceLaunchHistoryPage = page;
        if (entries.Length == 0) Text(parent, crashes ? "此实例没有已记录的异常退出。" : "此实例尚无持久运行记录。", 12, Muted, 30);
        foreach (var entry in entries.Skip(page * 12).Take(12))
        {
            var row = Stack(parent, "InstanceDiagnostics.History." + entry.Instance!.SessionId.ToString("N"), XsrUiOrientation.Vertical, 3);
            var caption = DiagnosticText(row, "InstanceDiagnostics.History.Timestamp", (entry.Instance.StartedAt ?? entry.Timestamp).ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture), 13, Ink, 28);
            DesktopLiteralText.Preserve(_shell.Tree, caption);
            InstanceDiagnosticFact(row, "HistorySession", "运行身份", entry.Instance.SessionId.ToString("N"));
            InstanceDiagnosticFact(row, "HistoryResult", "已记录结果", _shell.Renderer.LocalizeText(entry.Outcome switch
            {
                DiagnosticOperationOutcome.Started or DiagnosticOperationOutcome.Entered => "进行中",
                DiagnosticOperationOutcome.Completed => "已完成",
                DiagnosticOperationOutcome.Cancelled => "已取消",
                DiagnosticOperationOutcome.Unfinished => "尚无完成记录",
                _ => "失败或拒绝"
            }));
            if (entry.Instance.EndedAt is { } ended) InstanceDiagnosticFact(row, "HistoryEnded", "退出时间", ended.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture));
            if (entry.Instance.ExitCode is { } exit) InstanceDiagnosticFact(row, "HistoryExit", "退出码", exit.ToString(CultureInfo.InvariantCulture));
            if (entry.Instance.FailureCode is { } code) InstanceDiagnosticFact(row, "HistoryFailure", "问题分类", code);
            if (entry.Instance.LaunchDurationMilliseconds is { } duration) InstanceDiagnosticFact(row, "HistoryLaunchDuration", "进程启动耗时", duration.ToString(CultureInfo.InvariantCulture) + " ms");
        }
        if (pages > 1) ManagementButton(parent, $"{page + 1}/{pages} · " + _shell.Renderer.LocalizeText("下一页"), () =>
        {
            if (crashes) _instanceCrashHistoryPage = (page + 1) % pages; else _instanceLaunchHistoryPage = (page + 1) % pages;
            BuildSections(true);
        }, 144);
    }

    private void UpdateInstanceDiagnostics()
    {
        if (_selected != "diagnostics" || _instance != _instanceDiagnosticTarget)
        { if (_instanceDiagnosticTarget is not null) CancelInstanceDiagnostics(); return; }
        if (_instanceHistoryRead is not { IsCompleted: true } read) return;
        _instanceHistoryRead = null; _instanceHistoryStop?.Dispose(); _instanceHistoryStop = null;
        if (PendingQuery.Succeeded(read)) _instanceHistory = read.Result.Value!.Take(1024).ToArray();
        else _instanceHistoryError = "无法读取此实例的持久诊断历史。";
        BuildSections(true);
    }
}
