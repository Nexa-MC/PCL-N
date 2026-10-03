namespace Nexa.UI.Next;

public sealed partial class XsrUiRenderer
{
    private XsrUiEntityId _graphDrag;
    private XsrUiPoint _graphPress;
    private double _graphPanX, _graphPanY;
    private bool _graphMoved;
    private bool BeginGraphGesture(XsrUiEntityId entity, XsrUiPoint point)
    {
        if (_tree.GetComponent<XsrUiGraph>(entity) is not { } graph) return false;
        _graphDrag = entity; _graphPress = point; _graphPanX = graph.PanX; _graphPanY = graph.PanY; _graphMoved = false;
        return true;
    }
    private bool MoveGraphGesture(XsrUiPoint point)
    {
        if (!_graphDrag.IsAssigned) return false;
        if (!_tree.IsAlive(_graphDrag) || !IsInVisibleTree(_graphDrag)) { _graphDrag = default; return false; }
        var graph = _tree.GetComponent<XsrUiGraph>(_graphDrag)!;
        double dx = point.X - _graphPress.X, dy = point.Y - _graphPress.Y;
        if (!_graphMoved && dx * dx + dy * dy < 36) return true;
        _graphMoved = true; ClearPointerPress();
        graph.PanX = _graphPanX + dx; graph.PanY = _graphPanY + dy;
        _tree.MarkDirty(_graphDrag, XsrUiDirtyKinds.Paint);
        return true;
    }
    private bool EndGraphGesture(XsrUiPoint? point)
    {
        if (!_graphDrag.IsAssigned) return false;
        var entity = _graphDrag; _graphDrag = default;
        if (!_tree.IsAlive(entity) || point is null || _graphMoved) { ClearPointerPress(); return true; }
        var graph = _tree.GetComponent<XsrUiGraph>(entity)!;
        var node = _scene?.Nodes.FirstOrDefault(n => n.Entity == entity);
        if (node is not { } canvas || !canvas.Rect.Contains(point.Value)) { ClearPointerPress(); return true; }
        graph.Selected = graph.HitTest(point.Value, canvas.Rect);
        _tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
        ClearPointerPress(); Activate(entity);
        return true;
    }
}
