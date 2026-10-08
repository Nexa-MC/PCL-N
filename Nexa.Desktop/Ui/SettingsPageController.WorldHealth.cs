using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private Task<XsrResult<InstanceWorldHealth>>? _worldHealthRead;
    private InstanceWorldHealth? _worldHealth;
    private (string Instance, string World) _worldHealthTarget;
    private CancellationTokenSource? _worldHealthStop;
    private string? _worldHealthError;

    private void CancelWorldHealth()
    {
        _worldHealthStop?.Cancel(); _worldHealthStop?.Dispose(); _worldHealthStop = null;
        _worldHealthRead = null; _worldHealth = null; _worldHealthError = null; _worldHealthTarget = default;
    }

    private void BuildWorldHealth(XsrUiEntityId parent, InstanceContentEntry item)
    {
        var target = (Instance: _instance!, World: item.Name);
        if (_worldHealthTarget != target) { CancelWorldHealth(); _worldHealthTarget = target; }
        if (_queries.TryResolve(InstanceWorldHealthContract.Read, out var route)) ManagementButton(parent, "检查世界健康", () =>
        {
            if (_worldHealthRead is not null) return;
            _worldHealth = null; _worldHealthError = null; _worldHealthStop = new();
            _worldHealthRead = _queries.QueryAsync<InstanceWorldHealthQuery, InstanceWorldHealth>(route, new(target.Instance, target.World), cancellationToken: _worldHealthStop.Token).AsTask();
            WakeOnPlatformCompletion(_worldHealthRead); BuildSections(true);
        }, 128);
        if (_worldHealthRead is not null) Text(parent, "正在检查 region 分配与区块 NBT…", 12, Muted, 28);
        if (_worldHealthError is { } error) Text(parent, error, 12, Muted, 30);
        if (_worldHealth is not { } health) return;
        Text(parent, health.Healthy ? "已完成检查，未发现结构损坏。" : health.Complete ? "检查发现存档结构问题，原文件已保留。" : "检查不完整，不能确认世界健康。", 13, health.Healthy ? Ink : Muted, 32);
        ManagementFactIn(parent, "已检查 region", health.Regions.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ManagementFactIn(parent, "已检查区块", health.Chunks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (string issue in health.Issues.Take(10))
        { var line = Text(parent, issue, 12, Muted, 32); DesktopLiteralText.Preserve(_shell.Tree, line); }
        if (health.DetectedIssues > 10) Text(parent, "还有更多问题；未修改存档，请先保留备份。", 12, Muted, 30);
    }

    private void UpdateWorldHealth()
    {
        if (_worldHealthRead is not { IsCompleted: true } task) return;
        _worldHealthRead = null; _worldHealthStop?.Dispose(); _worldHealthStop = null;
        if (_instance != _worldHealthTarget.Instance || _contentDetail?.Name != _worldHealthTarget.World || _selected != "saves") return;
        if (PendingQuery.Succeeded(task)) _worldHealth = task.Result.Value;
        else _worldHealthError = "无法完成健康检查，请先关闭游戏并检查访问权限。";
        BuildSections(true);
    }
}
