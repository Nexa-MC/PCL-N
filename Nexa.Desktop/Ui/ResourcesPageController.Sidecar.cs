using Nexa.Sidecar.Protocol;
using Nexa.UI.Next;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Ui;

internal sealed partial class ResourcesPageController
{
    private Guid _moduleActivation;
    private readonly Dictionary<ushort, XsrUiEntityId> _moduleNodes = [];
    private readonly Dictionary<ushort, XsrUiEntityId> _moduleSubmits = [];
    private readonly Dictionary<ushort, XsrUiEntityId> _moduleLabels = [];
    private readonly Dictionary<ushort, XsrUiNodeView> _moduleViews = [];
    private readonly List<XsrUiEntityId> _moduleActions = [];
    private CancellationTokenSource _moduleSourceStop = new();
    private bool _moduleSourceVisible;

    private void SyncSidecarSource(bool visible)
    {
        if (!_visible)
            while (_pending.TryDequeue(out _)) { }
        if (visible == _moduleSourceVisible) return;
        _moduleSourceVisible = visible;
        if (!visible)
        {
            int pending = _pending.Count;
            for (int index = 0; index < pending && _pending.TryDequeue(out var source); index++)
                if (_visible && !_moduleActions.Contains(source)) _pending.Enqueue(source);
            _moduleActivation = Guid.Empty; _moduleRevision = -1;
        }
        _moduleSourceStop.Cancel(); _moduleSourceStop.Dispose(); _moduleSourceStop = new();
    }
    private void RetireSidecarSource()
    {
        _moduleSourceStop.Cancel();
        foreach (var entity in _moduleActions) _actions.Remove(entity);
        _moduleActions.Clear(); _moduleNodes.Clear(); _moduleSubmits.Clear(); _moduleLabels.Clear(); _moduleViews.Clear();
    }
    private void ProjectSidecarModules()
    {
        if (_sidecarUi is null) return;
        var snapshot = _store.Read<XsrUiModuleSnapshot>(_sidecarUi.ModuleState);
        if (snapshot.Revision == _moduleRevision) return;
        _moduleRevision = snapshot.Revision;
        var module = snapshot.Value?.ModuleAt(_sidecarUi.CardIndex);
        var slot = _entities["ResourceExtensionCard"];
        E(slot).IsVisible = module is not null;
        if (_moduleActivation != (module?.Activation ?? Guid.Empty))
        {
            RetireSidecarSource(); _moduleSourceStop.Dispose(); _moduleSourceStop = new();
            _moduleActivation = module?.Activation ?? Guid.Empty;
            foreach (var child in _shell.Tree.Children(slot).ToArray()) _shell.Tree.Destroy(child);
            if (module is not null)
            {
                LiteralText(slot, module.Card.Title, 14, Ink, 22, 600);
                var body = LiteralText(slot, module.Card.Body, 13, Muted, 0, lines: 0);
                E(body).Height = null; _shell.Tree.GetComponent<XsrUiVisualStyle>(body)!.WrapText = true;
                foreach (var node in module.Nodes) CreateSidecarNode(module.Activation, node, node.Parent == 0 ? slot : _moduleNodes[node.Parent]);
            }
            _shell.Tree.GetComponent<XsrUiScroll>(slot)!.OffsetY = 0;
        }
        if (module is not null)
            foreach (var node in module.Nodes) UpdateSidecarNode(node);
        _shell.Tree.MarkDirty(slot, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
    private void CreateSidecarNode(Guid activation, XsrUiNodeView node, XsrUiEntityId parent)
    {
        string name = "ResourcePluginNode." + node.Id;
        XsrUiEntityId entity;
        switch (node.Kind)
        {
            case SidecarUiNodeKind.Stack or SidecarUiNodeKind.Row:
                entity = Stack(parent, name, node.Kind == SidecarUiNodeKind.Row); break;
            case SidecarUiNodeKind.Button or SidecarUiNodeKind.Toggle:
                entity = Button(parent, name, node.Label, 150, () => RunSidecarAction(activation, node.Id));
                _moduleActions.Add(entity); DesktopLiteralText.Preserve(_shell.Tree, entity); break;
            case SidecarUiNodeKind.TextInput:
                _moduleLabels[node.Id] = LiteralText(parent, node.Label, 12, Muted, 22);
                entity = Input(parent, name, node.Label, 220);
                DesktopLiteralText.Preserve(_shell.Tree, entity);
                _shell.Tree.SetComponent(entity, new XsrUiTextInput { MaximumLength = 512 });
                _shell.Renderer.SetTextInputValue(entity, node.Value);
                var submit = Button(parent, name + ".Submit", "提交", 68, () => RunSidecarAction(activation, node.Id));
                _moduleActions.Add(submit); _moduleSubmits[node.Id] = submit; break;
            case SidecarUiNodeKind.Image:
                entity = Element(parent, name, XsrUiSemanticRole.Image, node.Label); E(entity).Width = 160; E(entity).Height = 120;
                DesktopLiteralText.Preserve(_shell.Tree, entity);
                if (node.Image is { } image)
                    _shell.Tree.SetComponent(entity, new XsrUiImage("")
                    {
                        Raster = new(image,
                        [new(new(0, 0, image.Width, image.Height), new(0, 0, 1, 1))])
                        { FitToBounds = true }
                    });
                break;
            default:
                entity = LiteralText(parent, node.Label, 13, Ink, 28); break;
        }
        _moduleNodes[node.Id] = entity;
    }
    private void UpdateSidecarNode(XsrUiNodeView node)
    {
        var entity = _moduleNodes[node.Id];
        E(entity).IsVisible = node.Visible;
        if (_moduleLabels.TryGetValue(node.Id, out var label)) E(label).IsVisible = node.Visible;
        if (_shell.Tree.GetComponent<XsrUiInput>(entity) is { } input) input.Enabled = node.Enabled;
        if (_moduleSubmits.TryGetValue(node.Id, out var submit))
        {
            E(submit).IsVisible = node.Visible;
            _shell.Tree.GetComponent<XsrUiInput>(submit)!.Enabled = node.Enabled;
            if (!_moduleViews.TryGetValue(node.Id, out var previous) || previous.Value != node.Value)
                _shell.Renderer.SetTextInputValue(entity, node.Value);
        }
        else if (node.Kind is SidecarUiNodeKind.Text or SidecarUiNodeKind.Button or SidecarUiNodeKind.Toggle)
        {
            string caption = node.Kind == SidecarUiNodeKind.Toggle ? (node.ToggleValue ? "✓ " : "□ ") + node.Label
                : node.Value.Length == 0 ? node.Label : node.Label.Length == 0 ? node.Value : node.Label + " " + node.Value;
            _shell.Tree.SetComponent(entity, new XsrUiText(caption) { Localize = false });
            _shell.Tree.SetComponent(entity, new XsrUiSemantic(node.Kind == SidecarUiNodeKind.Text ? XsrUiSemanticRole.Text : XsrUiSemanticRole.Button, caption) { Localize = false });
        }
        _moduleViews[node.Id] = node;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
    private void RunSidecarAction(Guid activation, ushort node)
    {
        if (_disposed || !_moduleSourceVisible || _sidecarUi is null || activation != _moduleActivation
            || !_moduleNodes.TryGetValue(node, out var entity) || !_shell.Tree.IsAlive(entity)) return;
        string? draft = _shell.Tree.GetComponent<XsrUiTextInput>(entity)?.ReadDraft();
        var token = _moduleSourceStop.Token;
        var task = _sidecarUi.Modules.DispatchAsync(_sidecarUi.CardIndex, activation, node, draft, token).AsTask();
        Wake(task, token);
        _ = ReportSidecarActionAsync(task, token);
    }
    private async Task ReportSidecarActionAsync(Task<Nexa.Xsr.XsrResult> task, CancellationToken token)
    {
        try
        {
            var result = await task.ConfigureAwait(false);
            if (!_disposed && !token.IsCancellationRequested && !result.IsSuccess)
                _presentation.Enqueue(() => { if (!token.IsCancellationRequested) _feedback?.Error(result.Error!.Message); });
        }
        catch (OperationCanceledException) { }
    }
}
