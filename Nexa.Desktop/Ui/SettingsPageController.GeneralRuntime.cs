using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private XsrUiEntityId _generalProtocolStatus;
    private DesktopProtocolObservation? _generalProtocolObservation;

    private void BuildGeneralRuntimeStates()
    {
        _generalProtocolStatus = default; _generalProtocolObservation = null;
        if (_instanceDirectory is not null || _selected != "general") return;
        var confirmation = FormGroup(_sections, "SettingsGeneral.Confirmations", "操作确认");
        Text(confirmation, "文件修改、恢复与数据发送前保留确认。", 12, Muted, 32);
        var protocol = FormGroup(_sections, "SettingsGeneral.Protocol", "nexacl:// URI");
        _generalProtocolStatus = Text(protocol, "尚未观察到协议注册。", 12, Muted, 32);
        UpdateGeneralRuntimeStates();
    }

    private void UpdateGeneralRuntimeStates()
    {
        if (!_visible || _instanceDirectory is not null || _selected != "general"
            || !_generalProtocolStatus.IsAssigned || !_shell.Tree.IsAlive(_generalProtocolStatus)) return;
        var observed = DesktopProtocolRegistration.Observation;
        if (Equals(_generalProtocolObservation, observed)) return;
        _generalProtocolObservation = observed;
        string message = !observed.Attempted ? "尚未观察到协议注册。" : observed.Pending ? "正在注册 nexacl:// 协议。"
            : observed.Succeeded ? "nexacl:// 协议注册已完成。" : "nexacl:// 协议注册未完成。";
        var text = _shell.Tree.GetComponent<XsrUiText>(_generalProtocolStatus)!;
        text.Localize = false;
        text.Content = _shell.Renderer.LocalizeText(message);
        if (observed.ObservedAt is { } timestamp) text.Content += " " + timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        if (observed.Detail is { Length: > 0 } detail) text.Content += " " + detail;
        var semantic = _shell.Tree.GetComponent<XsrUiSemantic>(_generalProtocolStatus);
        if (semantic is not null) { semantic.Label = text.Content; semantic.Localize = false; }
        _shell.Tree.MarkDirty(_generalProtocolStatus, XsrUiDirtyKinds.Paint);
    }
}
