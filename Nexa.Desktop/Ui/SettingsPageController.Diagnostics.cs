using System.Globalization;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

internal sealed record StateInspectorMetadata(string SemanticId, string Owner, string Kind, long Revision, string Availability);
internal sealed record StateInspectorCapture(int Total, IReadOnlyList<StateInspectorMetadata> Rows);

internal sealed partial class SettingsPageController
{
    private const int StateInspectorPageSize = 32;
    private const int StateInspectorMaximumRows = 2048;
    private static readonly XsrSemanticId RefreshRendererDiagnostics = XsrSemanticId.Parse("ui.settings.diagnostics.renderer.refresh");
    private static readonly XsrSemanticId RefreshStateDiagnostics = XsrSemanticId.Parse("ui.settings.diagnostics.state.refresh");
    private static readonly XsrSemanticId PreviousStateDiagnostics = XsrSemanticId.Parse("ui.settings.diagnostics.state.previous");
    private static readonly XsrSemanticId NextStateDiagnostics = XsrSemanticId.Parse("ui.settings.diagnostics.state.next");
    private readonly Dictionary<XsrUiEntityId, XsrSemanticId> _developerDiagnosticActions = [];
    private RendererDiagnosticsCapture? _rendererDiagnostics;
    private StateInspectorCapture? _stateInspector;
    private string? _stateInspectorError;
    private bool _developerDiagnosticsCaptured;
    private int _stateInspectorPage;
    private XsrUiEntityId _rendererDiagnosticsBody, _stateInspectorBody, _stateInspectorPrevious, _stateInspectorNext;

    private sealed record RendererDiagnosticsCapture(long SceneVersion, int LayoutVisits, int TreeCount,
        XsrUiColorScheme ColorScheme, bool ReducedMotion, bool OptionalMotionSuspended, bool EffectiveReducedMotion);

    private bool DeveloperDiagnosticsVisible => !_disposed && _visible && _instanceDirectory is null && _developer && _selected == "advanced";

    private static bool IsDeveloperDiagnosticsIntent(XsrSemanticId command) => command == RefreshRendererDiagnostics
        || command == RefreshStateDiagnostics || command == PreviousStateDiagnostics || command == NextStateDiagnostics;

    private void ResetDeveloperDiagnosticControls()
    {
        _developerDiagnosticActions.Clear();
        _rendererDiagnosticsBody = _stateInspectorBody = _stateInspectorPrevious = _stateInspectorNext = default;
    }

    private void RetireDeveloperDiagnostics()
    {
        ResetDeveloperDiagnosticControls();
        _rendererDiagnostics = null; _stateInspector = null; _stateInspectorError = null;
        _developerDiagnosticsCaptured = false; _stateInspectorPage = 0;
    }

    private void BuildDeveloperDiagnostics()
    {
        if (!DeveloperDiagnosticsVisible) { RetireDeveloperDiagnostics(); return; }
        bool Available(string id) => _catalog!.Entries.Any(entry => entry.Id == id && entry.DeveloperOnly
            && entry.Kind == SettingsCatalogEntryKind.State && entry.Availability == SettingsCapabilityAvailability.Available);
        bool renderer = Available("global.advanced.8d723d8dcdfb");
        bool inspector = Available("global.advanced.01c0534c8b37");
        if (!renderer && !inspector) return;
        if (!_developerDiagnosticsCaptured)
        {
            if (renderer) CaptureRendererDiagnostics();
            if (inspector) CaptureStateInspector();
            _developerDiagnosticsCaptured = true;
        }
        if (renderer) BuildRendererDiagnostics();
        if (inspector) BuildStateInspector();
    }

    private void CaptureRendererDiagnostics() => _rendererDiagnostics = new(_shell.Renderer.SceneVersion,
        _shell.Renderer.LastLayoutVisits, _shell.Tree.Count, _shell.Renderer.ColorScheme,
        _shell.Renderer.ReducedMotion, _shell.Renderer.OptionalMotionSuspended, _shell.Renderer.EffectiveReducedMotion);

    internal static StateInspectorCapture CaptureDeveloperStateMetadata(XsrStateStore store)
    {
        var snapshot = store.CaptureSnapshot();
        // Never access Value or retain the raw snapshot: diagnostics cannot render arbitrary payloads.
        var rows = snapshot.Entries.OrderBy(entry => entry.SemanticId.Value, StringComparer.Ordinal)
            .Take(StateInspectorMaximumRows)
            .Select(entry => new StateInspectorMetadata(entry.SemanticId.Value, entry.Owner, entry.Kind.ToString(),
                entry.Revision, entry.Availability.ToString())).ToArray();
        return new(snapshot.Entries.Count, Array.AsReadOnly(rows));
    }

    private void CaptureStateInspector()
    {
        _stateInspectorPage = 0; _stateInspectorError = null;
        try { _stateInspector = CaptureDeveloperStateMetadata(_store); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { _stateInspector = null; _stateInspectorError = "无法读取状态元数据，请重试。"; }
    }

    private void BuildRendererDiagnostics()
    {
        var card = SettingsCard("SettingsDiagnostics.Renderer", new(20, 14, 20, 14), 8);
        var toolbar = Stack(card, "SettingsDiagnostics.Renderer.Toolbar", XsrUiOrientation.Horizontal, 12);
        var title = Text(toolbar, "Renderer Diagnostics", 14, Ink, 36, 600);
        _shell.Tree.GetComponent<XsrUiElement>(title)!.Weight = 1;
        var refresh = RefreshIcon(toolbar, "SettingsDiagnostics.Renderer.Refresh", RefreshRendererDiagnostics);
        _shell.Tree.GetComponent<XsrUiSemantic>(refresh)!.Label = "刷新渲染器诊断";
        _developerDiagnosticActions[refresh] = RefreshRendererDiagnostics;
        Text(card, "场景与布局为上一帧，其余为捕获时信息；点击刷新更新。", 11, Muted, 24);
        _rendererDiagnosticsBody = Stack(card, "SettingsDiagnostics.Renderer.Body", XsrUiOrientation.Vertical, 4);
        BuildRendererDiagnosticFields();
    }

    private void BuildRendererDiagnosticFields()
    {
        if (_rendererDiagnostics is not { } snapshot) return;
        foreach (var child in _shell.Tree.Children(_rendererDiagnosticsBody).ToArray()) _shell.Tree.Destroy(child);
        void Field(string name, string label, string value)
        {
            var row = Stack(_rendererDiagnosticsBody, "SettingsDiagnostics.Renderer." + name, XsrUiOrientation.Horizontal, 12);
            var caption = Text(row, label, 12, Muted, 26);
            _shell.Tree.GetComponent<XsrUiElement>(caption)!.Width = 144;
            var text = DiagnosticText(row, "SettingsDiagnostics.Renderer." + name + ".Value", value, 12, Ink, 26);
            _shell.Tree.GetComponent<XsrUiElement>(text)!.Weight = 1;
        }
        Field("SceneVersion", "场景版本", snapshot.SceneVersion.ToString(CultureInfo.InvariantCulture));
        Field("LayoutVisits", "上次布局访问", snapshot.LayoutVisits.ToString(CultureInfo.InvariantCulture));
        Field("TreeCount", "树实体数量", snapshot.TreeCount.ToString(CultureInfo.InvariantCulture));
        Field("ColorScheme", "确认的颜色方案", _shell.Renderer.LocalizeText(snapshot.ColorScheme.IsDark ? "深色" : "浅色") + " · " + snapshot.ColorScheme.Accent);
        Field("Motion", "动态效果", _shell.Renderer.LocalizeText(snapshot.EffectiveReducedMotion ? "减少动态效果" : "正常动态效果"));
        Field("MotionPolicy", "动态策略", _shell.Renderer.LocalizeText("用户减少动态：") + _shell.Renderer.LocalizeText(snapshot.ReducedMotion ? "是" : "否")
            + " · " + _shell.Renderer.LocalizeText("暂停可选动态：") + _shell.Renderer.LocalizeText(snapshot.OptionalMotionSuspended ? "是" : "否"));
    }

    private void BuildStateInspector()
    {
        var card = SettingsCard("SettingsDiagnostics.State", new(20, 14, 20, 14), 8);
        var toolbar = Stack(card, "SettingsDiagnostics.State.Toolbar", XsrUiOrientation.Horizontal, 12);
        var title = Text(toolbar, "XSR State Inspector", 14, Ink, 36, 600);
        _shell.Tree.GetComponent<XsrUiElement>(title)!.Weight = 1;
        var refresh = RefreshIcon(toolbar, "SettingsDiagnostics.State.Refresh", RefreshStateDiagnostics);
        _shell.Tree.GetComponent<XsrUiSemantic>(refresh)!.Label = "刷新状态元数据";
        _developerDiagnosticActions[refresh] = RefreshStateDiagnostics;
        Text(card, "只读元数据；翻页沿用当前快照，点击刷新后重新读取。", 11, Muted, 24);
        var paging = Stack(card, "SettingsDiagnostics.State.Paging", XsrUiOrientation.Horizontal, 12);
        _stateInspectorPrevious = ActionButton(paging, "SettingsDiagnostics.State.Previous", "上一页", PreviousStateDiagnostics, 76);
        _stateInspectorNext = ActionButton(paging, "SettingsDiagnostics.State.Next", "下一页", NextStateDiagnostics, 76);
        _developerDiagnosticActions[_stateInspectorPrevious] = PreviousStateDiagnostics;
        _developerDiagnosticActions[_stateInspectorNext] = NextStateDiagnostics;
        _stateInspectorBody = Stack(card, "SettingsDiagnostics.State.Body", XsrUiOrientation.Vertical, 6);
        BuildStateInspectorPage();
    }

    private void BuildStateInspectorPage()
    {
        foreach (var child in _shell.Tree.Children(_stateInspectorBody).ToArray()) _shell.Tree.Destroy(child);
        var snapshot = _stateInspector;
        int count = snapshot?.Rows.Count ?? 0;
        int pages = Math.Max(1, (count + StateInspectorPageSize - 1) / StateInspectorPageSize);
        _stateInspectorPage = Math.Clamp(_stateInspectorPage, 0, pages - 1);
        _shell.Tree.GetComponent<XsrUiInput>(_stateInspectorPrevious)!.Enabled = _stateInspectorPage > 0;
        _shell.Tree.GetComponent<XsrUiInput>(_stateInspectorNext)!.Enabled = _stateInspectorPage + 1 < pages;
        string summary = _stateInspectorError
            ?? (snapshot is null ? "暂无状态元数据。" : $"第 {_stateInspectorPage + 1} / {pages} 页 · 共 {snapshot.Total} 项"
            + (count < snapshot.Total ? $"，显示前 {StateInspectorMaximumRows} 项" : ""));
        DiagnosticText(_stateInspectorBody, "SettingsDiagnostics.State.Summary", _shell.Renderer.LocalizeText(summary), 12, Muted, 26);
        if (snapshot is null) return;
        foreach (var entry in snapshot.Rows.Skip(_stateInspectorPage * StateInspectorPageSize).Take(StateInspectorPageSize))
        {
            string name = "SettingsDiagnostics.State.Entry." + entry.SemanticId;
            var row = Stack(_stateInspectorBody, name, XsrUiOrientation.Vertical, 2);
            DiagnosticText(row, name + ".Id", entry.SemanticId, 12, Ink, 24);
            var metadata = DiagnosticText(row, name + ".Metadata", _shell.Renderer.LocalizeText("拥有者：") + entry.Owner
                + " · " + _shell.Renderer.LocalizeText("类型：") + entry.Kind
                + " · " + _shell.Renderer.LocalizeText("修订：") + entry.Revision.ToString(CultureInfo.InvariantCulture)
                + " · " + _shell.Renderer.LocalizeText("可用性：") + entry.Availability, 11, Muted, 36);
            _shell.Tree.GetComponent<XsrUiVisualStyle>(metadata)!.WrapText = true;
            _shell.Tree.GetComponent<XsrUiText>(metadata)!.MaxLines = 2;
        }
        _shell.Tree.MarkDirty(_stateInspectorBody, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private XsrUiEntityId DiagnosticText(XsrUiEntityId parent, string name, string content, double size, XsrUiColor ink, double height)
    {
        var entity = Element(parent, name, XsrUiSemanticRole.Text, content, height: height);
        _shell.Tree.SetComponent(entity, new XsrUiText(content) { MaxLines = 1, TrimOverflow = true, Localize = false });
        _shell.Tree.GetComponent<XsrUiSemantic>(entity)!.Localize = false;
        Style(entity, XsrUiColor.Transparent, ink, 0, size);
        return entity;
    }

    private void HandleDeveloperDiagnosticsIntent(DesktopUiIntent intent)
    {
        if (!DeveloperDiagnosticsVisible || _store.Read<long>(_revisionId).Value != _revision
            || !_shell.Tree.IsAlive(intent.Source) || !_developerDiagnosticActions.TryGetValue(intent.Source, out var command)
            || command != intent.Command || _shell.Tree.GetComponent<XsrUiInput>(intent.Source)?.Enabled != true) return;
        if (command == RefreshRendererDiagnostics) { CaptureRendererDiagnostics(); BuildRendererDiagnosticFields(); }
        else if (command == RefreshStateDiagnostics) { CaptureStateInspector(); BuildStateInspectorPage(); }
        else { _stateInspectorPage += command == PreviousStateDiagnostics ? -1 : 1; BuildStateInspectorPage(); }
    }
}
