using System.Globalization;
using Nexa.Services.Logging;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private bool _instanceOperationView;
    private int _instanceOperationPage;

    private void BuildInstanceOperationHistory(XsrUiEntityId parent)
    {
        var entries = _instanceHistory!.Where(entry => entry.Instance is not null).OrderByDescending(entry => entry.Timestamp).ToArray();
        int pages = Math.Max(1, (entries.Length + 11) / 12);
        _instanceOperationPage = Math.Clamp(_instanceOperationPage, 0, pages - 1);
        Text(parent, "逐条显示此实例的已记录操作和阶段，保留同一运行中的所有事件。", 11, Muted, 34);
        if (entries.Length == 0) Text(parent, "此实例尚无持久操作阶段记录。", 12, Muted, 30);
        foreach (var (entry, index) in entries.Skip(_instanceOperationPage * 12).Take(12).Select((entry, index) => (entry, index)))
        {
            var row = Stack(parent, "InstanceDiagnostics.Operation." + index.ToString(CultureInfo.InvariantCulture), XsrUiOrientation.Vertical, 3);
            InstanceDiagnosticFact(row, "OperationTimestamp", "记录时间", entry.Timestamp.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture));
            InstanceDiagnosticFact(row, "OperationSession", "运行身份", entry.Instance!.SessionId.ToString("N"));
            InstanceDiagnosticFact(row, "OperationModule", "模块", entry.Module);
            InstanceDiagnosticFact(row, "OperationName", "操作", entry.Operation);
            InstanceDiagnosticFact(row, "OperationStage", "阶段", entry.Stage);
            InstanceDiagnosticFact(row, "OperationOutcome", "阶段结果", _shell.Renderer.LocalizeText(entry.Outcome switch
            {
                DiagnosticOperationOutcome.Started => "已开始",
                DiagnosticOperationOutcome.Entered => "进入阶段",
                DiagnosticOperationOutcome.Completed => "已完成",
                DiagnosticOperationOutcome.Cancelled => "已取消",
                DiagnosticOperationOutcome.Unfinished => "尚无完成记录",
                _ => "失败或拒绝"
            }));
        }
        if (pages > 1) ManagementButton(parent, $"{_instanceOperationPage + 1}/{pages} · " + _shell.Renderer.LocalizeText("下一页"), () =>
        { _instanceOperationPage = (_instanceOperationPage + 1) % pages; BuildSections(true); }, 144);
    }
}
