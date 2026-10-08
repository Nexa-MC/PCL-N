using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private sealed record AdvancedLaunchRead(MinecraftLaunchPlanDiagnosticSnapshot? Plan, InstanceLocalDocumentsSnapshot? Documents, string? Error);
    private Task<AdvancedLaunchRead>? _advancedLaunchReading;
    private CancellationTokenSource? _advancedLaunchStop;
    private AdvancedLaunchRead? _advancedLaunch;
    private string? _advancedLaunchInstance;
    private string _advancedLaunchTab = "plan";
    private int _advancedLaunchPage;

    private void CancelAdvancedLaunchDiagnostics()
    {
        _advancedLaunchStop?.Cancel(); _advancedLaunchStop?.Dispose(); _advancedLaunchStop = null;
        _advancedLaunchReading = null; _advancedLaunch = null; _advancedLaunchInstance = null;
        _advancedLaunchPage = 0;
    }

    private void UpdateAdvancedLaunchDiagnostics()
    {
        if (_selected != "diagnostics" || _instance != _advancedLaunchInstance)
        { if (_advancedLaunchInstance is not null) CancelAdvancedLaunchDiagnostics(); return; }
        if (_advancedLaunchReading is not { IsCompleted: true } read) return;
        _advancedLaunchReading = null;
        _advancedLaunch = read.IsCompletedSuccessfully ? read.Result : new(null, null, "读取未完成；请检查文件权限后重试。");
        _advancedLaunchStop?.Dispose(); _advancedLaunchStop = null;
        BuildSections();
    }

    private void BuildAdvancedLaunchDiagnostics()
    {
        if (_instance is null) return;
        var group = FormGroup(_sections, "AdvancedLaunchDiagnostics", "高级启动事实（只读）");
        Text(group, "读取执行器已捕获的脱敏计划和本地文档；不会刷新账户、下载依赖或启动游戏。", 12, Muted, 34);
        ManagementButton(group, "读取启动事实", ReadAdvancedLaunchDiagnostics, 124);
        if (_advancedLaunchReading is not null) { Text(group, "正在读取本地事实…", 12, Muted, 24); return; }
        if (_advancedLaunch is null) { Text(group, "点击读取；没有启动计划捕获时将显示未知。", 12, Muted, 24); return; }
        if (_advancedLaunch.Error is { } error) Text(group, error, 12, Muted, 34);
        var tabs = Stack(group, "AdvancedLaunchTabs", XsrUiOrientation.Horizontal, 5);
        foreach (var (id, label) in new[] { ("plan", "计划与命令"), ("classpath", "Classpath"), ("natives", "Natives"),
            ("manifest", "Manifest"), ("metadata", "实例元数据"), ("lockfile", "Lockfile"), ("trace", "启动时间线") })
            ManagementButton(tabs, label, () => { _advancedLaunchTab = id; _advancedLaunchPage = 0; BuildSections(); }, 102);
        var lines = AdvancedLaunchLines();
        _advancedLaunchPage = Math.Clamp(_advancedLaunchPage, 0, Math.Max(0, (lines.Count - 1) / 32));
        int first = _advancedLaunchPage * 32;
        for (int index = first; index < Math.Min(lines.Count, first + 32); index++)
        {
            var text = Text(group, lines[index], 12, Ink, 26);
            DesktopLiteralText.Preserve(_shell.Tree, text);
            _shell.Tree.GetComponent<XsrUiVisualStyle>(text)!.WrapText = false;
        }
        if (lines.Count > 32)
        {
            var pages = Stack(group, "AdvancedLaunchPagination", XsrUiOrientation.Horizontal, 8);
            ManagementButton(pages, "上一页事实", () => { _advancedLaunchPage--; BuildSections(); }, 96);
            ManagementButton(pages, "下一页事实", () => { _advancedLaunchPage++; BuildSections(); }, 96);
            Text(pages, (_advancedLaunchPage + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " / "
                + ((lines.Count - 1) / 32 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), 12, Muted, 26);
        }
    }

    private IReadOnlyList<string> AdvancedLaunchLines()
    {
        var plan = _advancedLaunch?.Plan;
        if (_advancedLaunchTab is "manifest" or "metadata" or "lockfile")
        {
            if (_advancedLaunch?.Documents is not { } documents) return ["本地文档未读取成功。"];
            List<string> values = [];
            foreach (var document in documents.Documents.Where(item => item.Kind == _advancedLaunchTab))
            {
                values.Add(document.RelativePath + " · " + document.Status);
                if (document.Truncated) values.Add("该文档超过显示预算，已截断；原文件未修改。");
                values.AddRange(document.Lines);
            }
            return values.Count > 0 ? values : ["未找到此类本地文档。"];
        }
        if (_advancedLaunchTab == "trace") return AdvancedObservedLaunchTrace(plan);
        if (plan?.CapturedAt is not { } captured) return ["此启动器尚未捕获该实例的执行器计划；无法推断实际命令。"];
        if (_advancedLaunchTab == "classpath") return plan.ClasspathEntries.Count == 0 ? ["捕获计划没有 Classpath 项。"] : plan.ClasspathEntries;
        if (_advancedLaunchTab == "natives") return plan.NativeArchives.Count == 0 ? ["捕获计划没有 Native 归档。"] : plan.NativeArchives;
        List<string> result = ["捕获时间：" + captured.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
            "捕获阶段：执行器已应用图形策略，进程启动之前；后续启动结果以诊断历史为准。",
            "Java：" + plan.JavaExecutablePath, "工作目录：" + plan.WorkingDirectory, "Main class：" + plan.MainClass,
            "加载器：" + plan.Loader, "堆请求 MiB：" + plan.HeapLimitMiB.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "安全启动：" + (plan.SafeLaunch ? "开启" : "关闭"),
            "环境变量名称（全部值均省略）：" + string.Join(", ", plan.EnvironmentNames),
            "参数按真实顺序逐项显示；身份/凭证值和钩子正文不保留："];
        if (plan.Truncated) result.Add("该计划超过捕获预算，显示不完整。");
        result.AddRange(plan.Arguments); return result;
    }

    private List<string> AdvancedObservedLaunchTrace(MinecraftLaunchPlanDiagnosticSnapshot? plan)
    {
        List<string> lines = ["此时间线限于本次启动器已观察事实，未观察阶段不会补造。"];
        if (plan?.CapturedAt is { } captured) lines.Add(captured.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " · 执行器捕获启动计划");
        if (_store.TryResolve(MinecraftProcessStateComposition.SessionsKey, out var state))
        {
            var sessions = _store.ReadCollection<MinecraftProcessSnapshot>(state).Items;
            for (int index = Math.Max(0, sessions.Count - 256); index < sessions.Count; index++)
            {
                var item = sessions[index];
                if (!Nexa.Core.PathIdentity.Comparer.Equals(item.InstanceDirectory, _instance)) continue;
                lines.Add(item.StartedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " · 进程启动 · " + item.SessionId + " · " + item.State);
                if (item.EndedAt is { } ended) lines.Add(ended.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " · 进程结束 · exit "
                    + (item.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "未知"));
            }
        }
        if (lines.Count == 1) lines.Add("尚无此实例的计划或进程观测。");
        return lines;
    }

    private void ReadAdvancedLaunchDiagnostics()
    {
        if (_advancedLaunchReading is not null || _instance is null) return;
        if (!_queries.TryResolve(MinecraftLaunchPlanDiagnosticContract.Query, out var planRoute)
            || !_queries.TryResolve(InstanceLocalDocumentsContract.Query, out var documentsRoute))
        { _advancedLaunch = new(null, null, "启动事实查询未注册。"); BuildSections(); return; }
        _advancedLaunchInstance = _instance; _advancedLaunchStop = new(); string instance = _instance;
        var cancellation = _advancedLaunchStop.Token;
        _advancedLaunchReading = ReadAsync(); WakeOnPlatformCompletion(_advancedLaunchReading); BuildSections();
        async Task<AdvancedLaunchRead> ReadAsync()
        {
            var plan = _queries.QueryAsync<MinecraftLaunchPlanDiagnosticQuery, MinecraftLaunchPlanDiagnosticSnapshot>(planRoute,
                new(instance), cancellationToken: cancellation).AsTask();
            var documents = _queries.QueryAsync<InstanceLocalDocumentsQuery, InstanceLocalDocumentsSnapshot>(documentsRoute,
                new(instance), cancellationToken: cancellation).AsTask();
            await Task.WhenAll(plan, documents).ConfigureAwait(false);
            return new(plan.Result.IsSuccess ? plan.Result.Value : null, documents.Result.IsSuccess ? documents.Result.Value : null,
                plan.Result.IsSuccess && documents.Result.IsSuccess ? null : "部分查询未完成；可用事实已保留。请重试。");
        }
    }
}
