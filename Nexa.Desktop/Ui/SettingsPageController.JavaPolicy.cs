using Nexa.Services.Settings;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private XsrUiEntityId _javaPolicyStatus;
    private long _javaPolicyRevision = long.MinValue;
    private SettingsOverride? _javaPolicyPreference;

    private void BuildJavaPolicyStates()
    {
        if (!IsJavaInventoryPage) return;
        var card = SettingsCard("JavaPolicy", new(16, 10, 16, 10), 6);
        Text(card, "Java 选择与兼容性", 14, Ink, 28, 600);
        Text(card, "自动模式选择兼容的 Java；手动首选仍须满足当前实例的要求。", 12, Muted, 42);
        Text(card, "每次启动都会检查所选运行时；不兼容的手动选择会明确拒绝，不会静默换用其他 Java。", 12, Muted, 42);
        _javaPolicyStatus = Element(card, "JavaPolicy.Current", XsrUiSemanticRole.Text, null, height: 32);
        _shell.Tree.SetComponent(_javaPolicyStatus, new XsrUiText("") { MaxLines = 1, TrimOverflow = true });
        Style(_javaPolicyStatus, XsrUiColor.Transparent, Ink, 0, 12);
        _javaPolicyRevision = long.MinValue;
        RefreshJavaPolicyState();
    }

    private void UpdateJavaPolicyStates()
    {
        if (!_visible || !IsJavaInventoryPage || !_shell.Tree.IsAlive(_javaPolicyStatus)) return;
        RefreshJavaPolicyState();
    }

    private void RefreshJavaPolicyState()
    {
        SettingsOverride? preference = _values?.Values.FirstOrDefault(value => value.Key == "java.runtime")?.Value;
        long revision = _values?.Revision ?? -1;
        if (revision == _javaPolicyRevision && preference == _javaPolicyPreference) return;
        _javaPolicyRevision = revision;
        _javaPolicyPreference = preference;
        string caption = preference is null ? "正在读取 Java 选择策略…"
            : preference.Mode == SettingsOverrideMode.Auto || string.IsNullOrWhiteSpace(preference.Value) || preference.Value == "auto"
                ? "当前使用自动选择策略。" : "已配置首选 Java 路径，启动时仍会验证兼容性。";
        var text = _shell.Tree.GetComponent<XsrUiText>(_javaPolicyStatus)!;
        if (text.Content == caption) return;
        text.Content = caption;
        _shell.Tree.GetComponent<XsrUiSemantic>(_javaPolicyStatus)!.Label = caption;
        _shell.Tree.MarkDirty(_javaPolicyStatus, XsrUiDirtyKinds.Paint);
    }
}
