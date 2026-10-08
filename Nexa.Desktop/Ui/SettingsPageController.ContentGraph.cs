using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private XsrUiEntityId _graphSearch, _graphCanvas, _graphBody;
    private string _graphFilter = "";
    private InstanceContentGraph? _graphIdentity;
    private XsrUiGraph? _graph;
    private readonly HashSet<XsrUiEntityId> _graphActions = [];
    private void ResetContentGraph()
    {
        _graphFilter = ""; _graphIdentity = null; _graph = null;
        _graphSearch = _graphCanvas = _graphBody = default;
    }
    private void ReleaseContentGraph()
    {
        ResetContentGraph();
        if (_management is { } snapshot) _management = snapshot with { ContentGraph = null };
    }
    private void BuildContentGraph(InstanceManagementSnapshot snapshot)
    {
        if (snapshot.ContentGraph is not { } graph) { Text(_sections, "正在读取依赖关系…", 13, Muted, 28); return; }
        if (!ReferenceEquals(_graphIdentity, graph))
        {
            _graphIdentity = graph;
            var nodes = graph.Nodes.Select(n => new XsrUiGraphNode(n.Key, n.Id, !n.Enabled ? Muted
                : n.InCycle ? new(207, 121, 33) : n.Dependencies.Any(d => d.State == InstanceDependencyState.Missing) ? new(196, 66, 62) : Blue));
            var edges = graph.Nodes.SelectMany(n => n.Dependencies.SelectMany(d => d.Providers.Select(p => new XsrUiGraphEdge(n.Key, graph.Nodes[p].Key))));
            _graph = new(nodes, edges); _graph.Fit(700, 380);
        }
        var bar = Stack(_sections, "ContentGraphToolbar", XsrUiOrientation.Horizontal, 8);
        _graphSearch = Element(bar, "ContentGraphSearch", XsrUiSemanticRole.TextInput, "搜索依赖", height: 36);
        _shell.Tree.GetComponent<XsrUiElement>(_graphSearch)!.Weight = 1;
        _shell.Tree.GetComponent<XsrUiElement>(_graphSearch)!.Padding = new(12, 0, 12, 0);
        _shell.Tree.SetComponent(_graphSearch, new XsrUiTextInput { Placeholder = "搜索模组或依赖" });
        _shell.Tree.SetComponent(_graphSearch, new XsrUiInput { Focusable = true, Clickable = true });
        Style(_graphSearch, new(241, 244, 248), Ink, 10, 13);
        _shell.Renderer.SetTextInputValue(_graphSearch, _graphFilter);
        GraphButton(bar, "GraphZoomOut", "缩小", () => ZoomGraph(.8), 60);
        GraphButton(bar, "GraphZoomIn", "放大", () => ZoomGraph(1.25), 60);
        GraphButton(bar, "GraphFit", "适应", () =>
        {
            var node = _shell.Renderer.Render().Nodes.FirstOrDefault(n => n.Entity == _graphCanvas);
            _graph!.Fit(node.Rect.Width, node.Rect.Height); _shell.Tree.MarkDirty(_graphCanvas, XsrUiDirtyKinds.Paint);
        }, 60);
        Text(_sections, $"{graph.Nodes.Count} 个身份 · 箭头指向依赖 · 节点越大，被依赖越多", 12, Muted, 24);
        _graphCanvas = Element(_sections, "ContentGraphCanvas", XsrUiSemanticRole.Content, "依赖图，拖动平移，点击节点查看关系", height: 380);
        Style(_graphCanvas, new(247, 249, 252), Ink, 14);
        _shell.Tree.SetComponent(_graphCanvas, _graph!);
        _shell.Tree.SetComponent(_graphCanvas, new XsrUiInput { Clickable = true, Focusable = true });
        _shell.Tree.SetComponent(_graphCanvas, new XsrUiCommandBinding(ManagementAction));
        _managementActions[_graphCanvas] = RenderGraphSelection; _graphActions.Add(_graphCanvas);
        _graphBody = Stack(_sections, "ContentGraphSelection", XsrUiOrientation.Vertical, 6);
        RenderGraphSelection();
        Text(_sections, "来自本地包声明，不代表实际加载或已验证版本兼容。", 12, Muted, 24);
        if (graph.Notice is { } notice) ContentName(_sections, notice, 12, null, 0, Muted, literal: false);
        if (graph.UnknownFiles > 0) Text(_sections, $"{graph.UnknownFiles} 个文件尚未识别。", 12, Muted, 24);
    }
    private void ZoomGraph(double factor)
    {
        if (_graph is null) return;
        _graph.Zoom = Math.Clamp(_graph.Zoom * factor, .05, 6);
        _shell.Tree.MarkDirty(_graphCanvas, XsrUiDirtyKinds.Paint);
    }
    private void UpdateContentGraph()
    {
        if (_selected != "contentgraph" || !_graphSearch.IsAssigned || !_shell.Tree.IsAlive(_graphSearch) || _graph is null) return;
        string draft = _shell.Tree.GetComponent<XsrUiTextInput>(_graphSearch)!.ReadDraft();
        if (draft == _graphFilter) return;
        _graph.Filter = _graphFilter = draft;
        _shell.Tree.MarkDirty(_graphCanvas, XsrUiDirtyKinds.Paint);
    }
    private void RenderGraphSelection()
    {
        if (!_graphBody.IsAssigned || !_shell.Tree.IsAlive(_graphBody)) return;
        foreach (var child in _shell.Tree.Children(_graphBody).ToArray())
        {
            _shell.Tree.Walk(child, entity => { _managementActions.Remove(entity); _graphActions.Remove(entity); return true; });
            _shell.Tree.Destroy(child);
        }
        var node = _graphIdentity?.Nodes.FirstOrDefault(n => n.Key == _graph?.Selected);
        if (node is null) { Text(_graphBody, "选择一个节点，查看依赖和被依赖关系。", 13, Muted, 28); return; }
        ContentName(_graphBody, node.Id + " · " + node.Version, 16, null, 0);
        Text(_graphBody, node.Enabled ? "已启用" : "已禁用", 12, Muted, 24);
        if (!node.DependenciesComplete) Text(_graphBody, "依赖声明尚未完整识别。", 12, Muted, 24);
        if (node.InCycle) Text(_graphBody, "存在依赖循环。", 12, Muted, 24);
        foreach (var edge in node.Dependencies)
        {
            var row = Stack(_graphBody, "GraphDependency", XsrUiOrientation.Horizontal, 8);
            ContentName(row, edge.Id + " " + edge.Requirement + " · " + _shell.Renderer.LocalizeText(DependencyLabel(edge.State)), 12, null, 0);
            foreach (int provider in edge.Providers.Take(8))
            {
                int key = _graphIdentity!.Nodes[provider].Key;
                GraphButton(row, "GraphProvider." + key, "定位", () => SelectGraphNode(key), 54);
            }
        }
        Text(_graphBody, $"被 {node.Consumers.Count} 个模组依赖", 13, Muted, 24);
        foreach (var consumer in node.Consumers)
        {
            var dependent = _graphIdentity!.Nodes[consumer.Node];
            GraphButton(_graphBody, "GraphConsumer." + dependent.Key, dependent.Id, () => SelectGraphNode(dependent.Key), 220);
        }
        _shell.Tree.MarkDirty(_graphBody, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
    private void SelectGraphNode(int key)
    {
        _graph!.Selected = key;
        var point = _graph.Snapshot().Points.First(n => n.Key == key);
        _graph.PanX = -point.X * _graph.Zoom; _graph.PanY = -point.Y * _graph.Zoom;
        _shell.Tree.MarkDirty(_graphCanvas, XsrUiDirtyKinds.Paint); RenderGraphSelection();
    }
    private void GraphButton(XsrUiEntityId parent, string key, string label, Action action, double width)
    {
        var button = ActionButton(parent, key, label, ManagementAction, width);
        _managementActions[button] = action; _graphActions.Add(button);
    }
    private static string DependencyLabel(InstanceDependencyState state) => state switch
    {
        InstanceDependencyState.Present => "本地存在，范围待验证",
        InstanceDependencyState.Disabled => "提供者已禁用",
        InstanceDependencyState.Missing => "缺失",
        InstanceDependencyState.Ambiguous => "多个候选",
        InstanceDependencyState.Runtime => "运行时提供",
        InstanceDependencyState.NestedCandidate => "内嵌候选",
        _ => "未知"
    };
}
