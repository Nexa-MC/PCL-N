using System.Globalization;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private void BuildRecoveryTimeline(InstanceManagementSnapshot snapshot)
    {
        if (_selected != "recovery") return;
        var group = FormGroup(_sections, "RecoveryTimeline", "恢复时间线");
        if (snapshot.RecoveryStorage is not { } storage || !storage.Complete)
        { Text(group, "时间线读取不完整，请刷新后重试。", 11, Muted, 28); return; }
        var points = storage.Snapshots.OrderBy(x => x.CapturedAt).ThenBy(x => x.Revision).TakeLast(16).ToArray();
        if (points.Length == 0) { Text(group, "尚无已保存的成功运行记录。", 11, Muted, 28); return; }
        Text(group, "按已保存快照的实际采集时间排列；文件数量变化表示记录差值。", 11, Muted, 40);
        InstanceRecoverySnapshotSummary? previous = null;
        foreach (var point in points)
        {
            var row = Stack(group, "RecoveryTimeline." + point.Revision, Nexa.UI.Next.XsrUiOrientation.Vertical, 3);
            string label = point.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
                + " · " + point.Files.ToString(CultureInfo.InvariantCulture) + " " + _shell.Renderer.LocalizeText("个文件")
                + " · " + RecoverySize(point.ContentBytes);
            if (point.Revision == snapshot.RecoveryComparison?.BaselineRevision) label += " · " + _shell.Renderer.LocalizeText("当前恢复基线");
            DesktopLiteralText.Preserve(_shell.Tree, Text(row, label, 12, Ink, 28));
            if (previous is not null)
                DesktopLiteralText.Preserve(_shell.Tree, Text(row, _shell.Renderer.LocalizeText("文件数量变化") + " · "
                    + (point.Files - previous.Files).ToString("+0;-0;0", CultureInfo.InvariantCulture), 11, Muted, 24));
            previous = point;
        }
        if (storage.Snapshots.Count > points.Length) Text(group, "显示最近 16 个时间点。", 11, Muted, 26);
        if (snapshot.RecoveryComparison is { UnavailableReason: null } current)
            Text(group, string.Format(CultureInfo.CurrentCulture, _shell.Renderer.LocalizeText("当前与恢复基线比较：{0} 项更改"), current.Changes.Count), 11, Muted, 28);
    }
}
