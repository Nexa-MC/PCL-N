using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private const int GraphPageSize = 12;
    private XsrUiEntityId _graphSearch, _graphBody;
    private string _graphFilter = "";
    private int _graphPage, _graphDependencyPage, _graphConsumerPage;
    private int? _graphSelected;
    private IReadOnlyList<int>? _graphProviders;
    private int _graphProviderOwner;
    private int _graphPreviousPage;
    private string _graphPreviousFilter = "";
    private InstanceContentGraph? _graphIdentity;
    private readonly HashSet<XsrUiEntityId> _graphActions = [];

    private void ResetContentGraph()
    {
        _graphFilter = ""; _graphPage = _graphDependencyPage = _graphConsumerPage = 0;
        _graphSelected = null; _graphIdentity = null;
        _graphProviders = null;
        _graphPreviousPage = 0; _graphPreviousFilter = "";
        _graphSearch = _graphBody = default;
    }

    private void ReleaseContentGraph()
    {
        ResetContentGraph();
        if (_management is { } snapshot) _management = snapshot with { ContentGraph = null };
    }

    private void BuildContentGraph(InstanceManagementSnapshot snapshot)
    {
        Text(_sections, "内容依赖", 20, Ink, 32, 600);
        ContentName(_sections, "以下关系来自本地包声明，不代表 Minecraft 已加载，也未验证版本范围；循环和缺失提示不会自动阻止启动。",
            13, null, 0, Muted, literal: false);
        if (snapshot.ContentGraph is not { } graph)
        {
            Text(_sections, "正在读取依赖关系…", 13, Muted, 28);
            return;
        }
        if (!ReferenceEquals(_graphIdentity, graph))
        {
            _graphIdentity = graph; _graphSelected = null;
            _graphProviders = null;
            _graphPage = _graphDependencyPage = _graphConsumerPage = 0;
        }
        if (graph.Notice is { } notice) ContentName(_sections, notice, 13, null, 0, Muted, literal: false);
        if (graph.UnknownFiles > 0) ManagementFact("未识别文件", graph.UnknownFiles.ToString(System.Globalization.CultureInfo.CurrentCulture));
        var searchRow = Stack(_sections, "ContentGraphSearchRow", XsrUiOrientation.Horizontal, 10);
        _graphSearch = Element(searchRow, "ContentGraphSearch", XsrUiSemanticRole.TextInput, "搜索依赖", height: 36);
        _shell.Tree.GetComponent<XsrUiElement>(_graphSearch)!.Weight = 1;
        _shell.Tree.GetComponent<XsrUiElement>(_graphSearch)!.Padding = new(12, 0, 12, 0);
        _shell.Tree.SetComponent(_graphSearch, new XsrUiTextInput { Placeholder = "搜索模组 ID、版本或依赖" });
        _shell.Tree.SetComponent(_graphSearch, new XsrUiInput { Focusable = true, Clickable = true });
        Style(_graphSearch, new(241, 244, 248), Ink, 10, 13);
        _shell.Renderer.SetTextInputValue(_graphSearch, _graphFilter);
        _graphBody = Stack(_sections, "ContentGraphBody", XsrUiOrientation.Vertical, 10);
        RenderContentGraph();
    }

    private void UpdateContentGraph()
    {
        if (_selected != "contentgraph" || !_graphSearch.IsAssigned || !_shell.Tree.IsAlive(_graphSearch)) return;
        string draft = _shell.Tree.GetComponent<XsrUiTextInput>(_graphSearch)!.ReadDraft();
        if (draft == _graphFilter) return;
        _graphFilter = draft; _graphPage = 0; _graphSelected = null;
        RenderContentGraph();
        _shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY = 0;
    }

    private void RenderContentGraph()
    {
        if (_graphIdentity is not { } graph || !_graphBody.IsAssigned || !_shell.Tree.IsAlive(_graphBody)) return;
        foreach (var entity in _graphActions) _managementActions.Remove(entity);
        _graphActions.Clear();
        foreach (var child in _shell.Tree.Children(_graphBody).ToArray()) _shell.Tree.Destroy(child);
        if (_graphSelected is { } key && key >= 0 && key < graph.Nodes.Count)
        {
            GraphButton(_graphBody, "ContentGraphBack", "返回依赖列表", () =>
            { _graphSelected = null; _graphProviders = null; RenderContentGraph(); }, 120);
            var node = graph.Nodes[key];
            GraphIdentity(_graphBody, node);
            if (!node.DependenciesComplete) Text(_graphBody, "此模组的依赖声明尚未完整识别。", 13, Muted, 28);
            Text(_graphBody, "依赖前置", 16, Ink, 28, 600);
            if (node.Dependencies.Count == 0) Text(_graphBody, "未发现必需依赖声明。", 13, Muted, 28);
            _graphDependencyPage = ClampPage(_graphDependencyPage, node.Dependencies.Count);
            foreach (var edge in node.Dependencies.Skip(_graphDependencyPage * GraphPageSize).Take(GraphPageSize))
            {
                var row = Stack(_graphBody, "ContentGraphDependency", XsrUiOrientation.Vertical, 4);
                GraphLiteral(row, edge.Id + "  " + edge.Requirement);
                Text(row, DependencyLabel(edge.State), 12, Muted, 24);
                foreach (int provider in edge.Providers.Take(2))
                {
                    int target = provider;
                    var provided = graph.Nodes[target];
                    GraphButton(row, "ContentGraphProvider." + target, "查看提供者", () => SelectGraphNode(target), 100);
                    GraphLiteral(row, provided.Id + " · " + provided.Version, 12);
                }
                if (edge.Providers.Count > 2)
                    GraphButton(row, "ContentGraphProviders." + edge.Id, "查看全部候选", () =>
                    {
                        _graphPreviousPage = _graphPage; _graphPreviousFilter = _graphFilter;
                        _graphProviders = edge.Providers; _graphProviderOwner = node.Key;
                        _graphSelected = null; _graphPage = 0; _graphFilter = "";
                        _shell.Renderer.SetTextInputValue(_graphSearch, "");
                        RenderContentGraph();
                        _shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY = 0;
                    }, 110);
            }
            GraphPagination("Dependencies", node.Dependencies.Count, _graphDependencyPage, page =>
            { _graphDependencyPage = page; RenderContentGraph(); });
            Text(_graphBody, "被哪些模组依赖", 16, Ink, 28, 600);
            var consumers = node.Consumers;
            if (consumers.Count == 0) Text(_graphBody, "未发现依赖此模组的声明。", 13, Muted, 28);
            _graphConsumerPage = ClampPage(_graphConsumerPage, consumers.Count);
            foreach (var consumer in consumers.Skip(_graphConsumerPage * GraphPageSize).Take(GraphPageSize))
            {
                var dependent = graph.Nodes[consumer.Node];
                var dependency = dependent.Dependencies[consumer.Dependency];
                var row = Stack(_graphBody, "ContentGraphConsumer", XsrUiOrientation.Vertical, 4);
                GraphLiteral(row, dependent.Id + " → " + dependency.Id);
                Text(row, DependencyLabel(dependency.State), 12, Muted, 24);
                if (!dependent.Enabled) Text(row, "消费者已停用", 12, Muted, 24);
                int target = consumer.Node;
                GraphButton(row, "ContentGraphConsumer." + target, "查看消费者", () => SelectGraphNode(target), 100);
            }
            GraphPagination("Consumers", consumers.Count, _graphConsumerPage, page =>
            { _graphConsumerPage = page; RenderContentGraph(); });
        }
        else
        {
            string filter = _graphFilter.Trim();
            IEnumerable<InstanceContentNode> source = _graphProviders is { } providers
                ? providers.Select(providerKey => graph.Nodes[providerKey]) : graph.Nodes;
            if (_graphProviders is not null)
                GraphButton(_graphBody, "ContentGraphProvidersBack", "返回依赖关系", () => SelectGraphNode(_graphProviderOwner), 120);
            var matches = source.Where(node => filter.Length == 0
                || node.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || node.Version.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || node.Dependencies.Any(edge => edge.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
            Text(_graphBody, $"共 {matches.Length} 个模组身份", 13, Muted, 28);
            if (matches.Length == 0) Text(_graphBody, "没有匹配的模组身份。", 13, Muted, 28);
            _graphPage = ClampPage(_graphPage, matches.Length);
            foreach (var node in matches.Skip(_graphPage * GraphPageSize).Take(GraphPageSize))
            {
                var card = Stack(_graphBody, "ContentGraphNode." + node.Key, XsrUiOrientation.Vertical, 4);
                Style(card, White, Ink, 12);
                _shell.Tree.GetComponent<XsrUiElement>(card)!.Padding = new(14, 10, 14, 10);
                GraphIdentity(card, node);
                int selectedKey = node.Key;
                GraphButton(card, "ContentGraphDetails." + selectedKey, "查看关系", () => SelectGraphNode(selectedKey), 86);
            }
            GraphPagination("Nodes", matches.Length, _graphPage, page => { _graphPage = page; RenderContentGraph(); });
        }
        _shell.Tree.MarkDirty(_graphBody, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private void GraphIdentity(XsrUiEntityId parent, InstanceContentNode node)
    {
        GraphLiteral(parent, node.Id + " · " + node.Version, 15);
        Text(parent, node.Enabled ? node.NestedCandidate ? "未验证的内嵌候选" : "本地已启用" : "已停用", 12, Muted, 24);
        if (node.InCycle) Text(parent, "存在依赖循环，仅供排查。", 12, Muted, 24);
    }

    private void GraphLiteral(XsrUiEntityId parent, string text, double size = 13) =>
        ContentName(parent, RecoveryExplanation.Display(text), size, null, 0);

    private void SelectGraphNode(int key)
    {
        if (_graphProviders is not null)
        {
            _graphPage = _graphPreviousPage; _graphFilter = _graphPreviousFilter;
            _shell.Renderer.SetTextInputValue(_graphSearch, _graphFilter);
        }
        _graphSelected = key; _graphProviders = null; _graphDependencyPage = _graphConsumerPage = 0;
        RenderContentGraph();
        _shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY = 0;
    }

    private void GraphButton(XsrUiEntityId parent, string name, string label, Action action, double width = 84)
    {
        var entity = ActionButton(parent, name, label, ManagementAction, width);
        _managementActions[entity] = action; _graphActions.Add(entity);
    }

    private void GraphPagination(string kind, int count, int page, Action<int> select)
    {
        int pages = Math.Max(1, (count + GraphPageSize - 1) / GraphPageSize);
        if (pages == 1) return;
        var row = Stack(_graphBody, "ContentGraphPagination." + kind, XsrUiOrientation.Horizontal, 10);
        Text(row, $"{page + 1}/{pages}", 12, Muted, 28);
        if (page > 0) GraphButton(row, "ContentGraphPrevious." + kind, "上一页", () => select(page - 1), 70);
        if (page + 1 < pages) GraphButton(row, "ContentGraphNext." + kind, "下一页", () => select(page + 1), 70);
    }

    private static int ClampPage(int page, int count) => Math.Clamp(page, 0, Math.Max(0, (count - 1) / GraphPageSize));
    private static string DependencyLabel(InstanceDependencyState state) => state switch
    {
        InstanceDependencyState.Present => "本地存在，版本范围尚未验证",
        InstanceDependencyState.Disabled => "提供者已停用",
        InstanceDependencyState.Missing => "本地缺失",
        InstanceDependencyState.Ambiguous => "存在多个提供者，关系有歧义",
        InstanceDependencyState.Runtime => "由游戏、Java 或加载器提供，版本范围尚未验证",
        InstanceDependencyState.NestedCandidate => "内嵌候选，尚未确认实际加载",
        _ => "关系未知"
    };
}
