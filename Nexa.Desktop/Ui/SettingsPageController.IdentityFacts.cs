using Nexa.Services.Minecraft.Process;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private void BuildObservedInstanceHistory(XsrUiEntityId parent)
    {
        if (!_store.TryResolve(MinecraftProcessStateComposition.SessionsKey, out var state)) return;
        var items = _store.ReadCollection<MinecraftProcessSnapshot>(state).Items;
        DateTimeOffset? latest = null; double seconds = 0; int count = 0;
        for (int index = Math.Max(0, items.Count - 256); index < items.Count; index++)
        {
            var item = items[index];
            if (!Nexa.Core.PathIdentity.Comparer.Equals(item.InstanceDirectory, _instance)) continue;
            latest = latest is null || item.StartedAt > latest.Value ? item.StartedAt : latest;
            seconds += Math.Max(0, ((item.EndedAt ?? DateTimeOffset.UtcNow) - item.StartedAt).TotalSeconds);
            count++;
        }
        ManagementFactIn(parent, "最近启动（本次启动器）", latest is { } at
            ? at.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) : "本次启动器尚未启动此实例");
        ManagementFactIn(parent, "已观察进程用时（截至当前快照）", (seconds / 60).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " 分钟");
        Text(parent, "统计限于本次启动器最近 256 条进程记录，" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 条属于此实例；先前历史未记录。", 11, Muted, 34);
        if (_offlineReport is { } report)
            ManagementFactIn(parent, "实例健康（本地依赖）", report.Ready ? "离线依赖校验通过" : report.Complete ? "发现缺失或校验问题" : "检查不完整");
        else ManagementFactIn(parent, "实例健康（本地依赖）", "尚未检查，请运行下方离线就绪检查");
        if (_values?.Values.FirstOrDefault(value => value.Key == "game.memory") is { } memory)
            ManagementFactIn(parent, "有效启动内存", memory.Value.Mode == Nexa.Services.Settings.SettingsOverrideMode.Auto
                ? "自动分配（启动时计算）" : (memory.Value.Value ?? "未知") + " MiB · " + memory.Source);
    }
}
