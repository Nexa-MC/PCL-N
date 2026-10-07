using Nexa.Xsr;

namespace Nexa.UI.Next;

public sealed record XsrUiContextMenuItem(string Label, XsrSemanticId Command, bool IsEnabled = true);

/// <summary>Immutable native-menu semantics. Native clicks still enter the renderer's admission path.</summary>
public sealed class XsrUiContextMenu
{
    public XsrUiContextMenu(IEnumerable<XsrUiContextMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        XsrUiContextMenuItem[] snapshot = items.Take(33).ToArray();
        if (snapshot.Length is 0 or > 32 || snapshot.Any(item => string.IsNullOrWhiteSpace(item.Label) || !item.Command.IsAssigned))
            throw new ArgumentException("A context menu needs 1–32 named command items.", nameof(items));
        Items = Array.AsReadOnly(snapshot);
    }
    public IReadOnlyList<XsrUiContextMenuItem> Items { get; }
}

public sealed record XsrUiContextMenuSnapshot(XsrUiEntityId Source, IReadOnlyList<XsrUiContextMenuItem> Items);

public sealed partial class XsrUiRenderer
{
    public XsrUiContextMenuSnapshot? ContextMenuAt(XsrUiPoint point) => ContextMenuFor(HitTest(point));

    public XsrUiContextMenuSnapshot? ContextMenuFor(XsrUiEntityId entity)
    {
        while (entity.IsAssigned && _tree.IsAlive(entity) && IsInVisibleTree(entity))
        {
            if (!IsEnabled(_tree.GetComponent<XsrUiInput>(entity))) return null;
            if (_scene?.Nodes.FirstOrDefault(node => node.Entity == entity).ContextMenu is { } menu) return menu;
            if (_tree.GetComponent<XsrUiOverlayLayer>(entity)?.IsModal == true) return null;
            entity = _tree.Parent(entity);
        }
        return null;
    }

    public bool InvokeContextMenu(XsrUiEntityId source, XsrSemanticId command)
    {
        if (_sink is null || !_tree.IsAlive(source) || !IsInVisibleTree(source)
            || !IsEnabled(_tree.GetComponent<XsrUiInput>(source))) return false;
        if (source == _root && _tree.Children(_root).Any(entity => IsVisible(entity)
            && _tree.GetComponent<XsrUiOverlayLayer>(entity)?.IsModal == true)) return false;
        XsrUiContextMenu? menu = _tree.GetComponent<XsrUiContextMenu>(source);
        if (menu is null || !menu.Items.Any(item => item.Command == command && item.IsEnabled)) return false;
        _sink.Emit(command, source, XsrCorrelationId.Create());
        return true;
    }

    private XsrUiContextMenuSnapshot? ProjectContextMenu(XsrUiEntityId entity, XsrUiContextMenu? menu) =>
        menu is null ? null : new(entity, Array.AsReadOnly(menu.Items.Select(item =>
            item with { Label = LocalizeText(item.Label) }).ToArray()));
}
