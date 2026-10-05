using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Nexa.UI.Next.Backend.Avalonia;

internal sealed partial class AvaloniaUiSceneNodeControl
{
    private void DrawGraph(DrawingContext context, Rect rect, XsrUiGraphSnapshot graph)
    {
        using var clip = context.PushClip(rect);
        var positions = graph.Points.ToDictionary(n => n.Key, n => new Point(
            rect.Center.X + graph.PanX + n.X * graph.Zoom, rect.Center.Y + graph.PanY + n.Y * graph.Zoom));
        var nodes = graph.Points.ToDictionary(n => n.Key);
        var line = new Pen(new SolidColorBrush(Color.FromArgb(55, 83, 105, 135)), 1);
        var activeLine = new Pen(new SolidColorBrush(Color.FromArgb(180, 18, 104, 216)), 1.4);
        foreach (var edge in graph.Edges)
        {
            Point from = positions[edge.From], to = positions[edge.To];
            if (Math.Max(from.X, to.X) < rect.Left || Math.Min(from.X, to.X) > rect.Right
                || Math.Max(from.Y, to.Y) < rect.Top || Math.Min(from.Y, to.Y) > rect.Bottom) continue;
            Vector delta = to - from; double length = delta.Length;
            if (length < .01) continue;
            Vector direction = delta / length;
            double radius = Math.Max(2, nodes[edge.To].Radius * graph.Zoom);
            Point end = to - direction * (radius + 2);
            var pen = graph.Selected == edge.From || graph.Selected == edge.To ? activeLine : line;
            context.DrawLine(pen, from + direction * Math.Max(2, nodes[edge.From].Radius * graph.Zoom), end);
            Vector side = new(-direction.Y, direction.X);
            context.DrawLine(pen, end, end - direction * 5 + side * 2.5);
            context.DrawLine(pen, end, end - direction * 5 - side * 2.5);
        }
        int labels = 0;
        var brushes = new Dictionary<XsrUiColor, IBrush>();
        foreach (var node in graph.Points)
        {
            Point point = positions[node.Key]; double radius = Math.Max(2, node.Radius * graph.Zoom);
            if (point.X + radius < rect.Left || point.X - radius > rect.Right || point.Y + radius < rect.Top || point.Y - radius > rect.Bottom) continue;
            bool match = graph.Filter.Length > 0 && node.Label.Contains(graph.Filter, StringComparison.OrdinalIgnoreCase);
            bool selected = graph.Selected == node.Key;
            if (!brushes.TryGetValue(node.Color, out var brush)) brushes[node.Color] = brush = Brush(node.Color) ?? Brushes.SlateGray;
            context.DrawEllipse(brush, selected || match ? activeLine : null, point, radius, radius);
            if (selected || match && labels++ < 20)
            {
                var label = new FormattedText(node.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    CachedTypeface(FontWeight.SemiBold), 12, Brush(_node.ColorScheme.Foreground(new(94, 110, 130))))
                { MaxTextWidth = 200, MaxLineCount = 1 };
                context.DrawText(label, new Point(point.X + radius + 5, point.Y - 8));
            }
        }
    }
}
