namespace Nexa.UI.Next;

/// <summary>Domain-neutral identity and evidence color of a directed graph point.</summary>
public sealed record XsrUiGraphNode(int Key, string Label, XsrUiColor Color);
public readonly record struct XsrUiGraphEdge(int From, int To);
public readonly record struct XsrUiGraphPoint(int Key, string Label, XsrUiColor Color, double X, double Y, double Radius);
public sealed record XsrUiGraphSnapshot(IReadOnlyList<XsrUiGraphPoint> Points, IReadOnlyList<XsrUiGraphEdge> Edges,
    double Zoom, double PanX, double PanY, int? Selected, string Filter);

/// <summary>Bounded, deterministic scatter layout. One canvas, no per-point controls or idle simulation.</summary>
public sealed class XsrUiGraph
{
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<XsrUiGraphPoint> _points;
    private readonly IReadOnlyList<XsrUiGraphEdge> _edges;
    private XsrUiGraphSnapshot? _snapshot;
    public XsrUiGraph(IEnumerable<XsrUiGraphNode> nodes, IEnumerable<XsrUiGraphEdge> edges)
    {
        var identities = nodes.Take(10001).ToArray();
        var links = edges.Take(50001).Distinct().ToArray();
        if (identities.Length > 10000 || links.Length > 50000 || identities.Select(n => n.Key).Distinct().Count() != identities.Length)
            throw new ArgumentException("Graph exceeds its capacity or contains duplicate identities.");
        var incoming = identities.ToDictionary(n => n.Key, _ => 0);
        foreach (var edge in links)
        {
            if (!incoming.ContainsKey(edge.From) || !incoming.TryGetValue(edge.To, out int degree)) throw new ArgumentException("Graph edge has no endpoint.");
            incoming[edge.To] = degree + 1;
        }
        // Highly shared dependencies occupy the center. Golden-angle spacing avoids identical
        // polar axes and is linear in graph size; it remains stable across viewport resizes.
        var ordered = identities.OrderByDescending(n => incoming[n.Key]).ThenBy(n => n.Key).ToArray();
        _points = Array.AsReadOnly(ordered.Select((node, i) =>
        {
            double distance = Math.Sqrt(i) * 25, angle = i * 2.399963229728653;
            return new XsrUiGraphPoint(node.Key, node.Label, node.Color,
                Math.Cos(angle) * distance, Math.Sin(angle) * distance, 5 + Math.Min(15, Math.Sqrt(incoming[node.Key]) * 2.5));
        }).ToArray());
        _edges = Array.AsReadOnly(links);
    }
    public double Zoom { get; set; } = 1;
    public double PanX { get; set; }
    public double PanY { get; set; }
    public int? Selected { get; set; }
    public string Filter { get; set; } = "";
    public void Fit(double width, double height)
    {
        double extent = _points.Count == 0 ? 50 : _points.Max(p => Math.Max(Math.Abs(p.X), Math.Abs(p.Y)) + p.Radius);
        Zoom = Math.Clamp((Math.Min(width, height) - 40) / (extent * 2), .05, 3);
        PanX = PanY = 0;
    }
    public XsrUiGraphSnapshot Snapshot()
    {
        double zoom = Math.Clamp(Zoom, .05, 6);
        if (_snapshot is { } cached && cached.Zoom == zoom && cached.PanX == PanX && cached.PanY == PanY
            && cached.Selected == Selected && cached.Filter == Filter) return cached;
        return _snapshot = new(_points, _edges, zoom, PanX, PanY, Selected, Filter);
    }
    public int? HitTest(XsrUiPoint point, XsrUiRect viewport)
    {
        if (!viewport.Contains(point)) return null;
        double zoom = Math.Clamp(Zoom, .05, 6), x = (point.X - viewport.X - viewport.Width / 2 - PanX) / zoom,
            y = (point.Y - viewport.Y - viewport.Height / 2 - PanY) / zoom;
        XsrUiGraphPoint? nearest = null; double best = double.PositiveInfinity;
        foreach (var node in _points)
        {
            double distance = Math.Pow(node.X - x, 2) + Math.Pow(node.Y - y, 2);
            if (distance <= Math.Pow(Math.Max(node.Radius, 7 / zoom), 2) && distance < best) { nearest = node; best = distance; }
        }
        return nearest?.Key;
    }
}
