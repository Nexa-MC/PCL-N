namespace Nexa.UI.Next;

public sealed partial class XsrUiRenderer
{
    private XsrUiEntityId _segmentDrag;
    public void SetSegmentExpanded(XsrUiEntityId entity, bool expanded, bool immediate = false)
    {
        if (_tree.GetComponent<XsrUiSegmentReveal>(entity) is not { } reveal) return;
        if (reveal.Expanded == expanded && !immediate) return;
        reveal.Expanded = expanded;
        if (_tree.GetComponent<XsrUiInput>(entity) is { } input) input.Enabled = expanded;
        if (EffectiveReducedMotion || immediate) SetSegmentRevealProgress(entity, expanded ? 1 : 0);
        else
        {
            _tree.GetComponent<XsrUiElement>(entity)!.IsVisible = true;
            // Keep one visible pixel so a collapsed segment can enter the host's animation list.
            if (expanded && reveal.Progress == 0) SetSegmentRevealProgress(entity, .001);
            _tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        }
    }
    public double GetSegmentRevealProgress(XsrUiEntityId entity) => _tree.GetComponent<XsrUiSegmentReveal>(entity)?.Progress ?? 0;
    public void SetSegmentRevealProgress(XsrUiEntityId entity, double progress)
    {
        if (!_tree.IsAlive(entity) || _tree.GetComponent<XsrUiSegmentReveal>(entity) is not { } reveal) return;
        reveal.Progress = EffectiveReducedMotion ? reveal.Expanded ? 1 : 0 : Math.Clamp(progress, 0, 1);
        XsrUiElement element = _tree.GetComponent<XsrUiElement>(entity)!;
        element.Width = reveal.Width * reveal.Progress;
        element.IsVisible = reveal.Expanded || reveal.Progress > .001;
        _tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
    private bool MoveSegmentForKey(XsrUiKey key)
    {
        if (key is not (XsrUiKey.Left or XsrUiKey.Right) || !_focused.IsAssigned) return false;
        XsrUiEntityId parent = _tree.Parent(_focused);
        if (!parent.IsAssigned || _tree.GetComponent<XsrUiSegmentedTrack>(parent) is null) return false;
        XsrUiEntityId[] segments = _tree.Children(parent).Where(entity => IsInVisibleTree(entity)
            && _tree.GetComponent<XsrUiInput>(entity) is { Clickable: true, Enabled: true }).ToArray();
        int current = Array.IndexOf(segments, _focused);
        if (current < 0) return false;
        int next = Math.Clamp(current + (key == XsrUiKey.Right ? 1 : -1), 0, segments.Length - 1);
        if (next != current) { Focus(segments[next]); Activate(segments[next]); }
        return true;
    }
    private void RevealSegment(XsrUiEntityId parent, XsrUiEntityId child)
    {
        if (_tree.GetComponent<XsrUiScroll>(parent) is not { } scroll || !_paintRects.TryGetValue(parent.Index, out XsrUiRect viewport)
            || !_paintRects.TryGetValue(child.Index, out XsrUiRect target)) return;
        double delta = target.X < viewport.X ? target.X - viewport.X
            : target.X + target.Width > viewport.X + viewport.Width ? target.X + target.Width - viewport.X - viewport.Width : 0;
        if (delta == 0) return;
        scroll.OffsetX = Math.Max(0, scroll.OffsetX + delta);
        _tree.MarkDirty(parent, XsrUiDirtyKinds.Layout);
    }
    private bool BeginSegmentDrag(XsrUiPoint point)
    {
        if (_scene is null) return false;
        foreach (XsrUiSceneNode node in _scene.Nodes)
        {
            if (!_tree.IsAlive(node.Entity) || !node.IsAccessible) continue;
            if (_tree.GetComponent<XsrUiSegmentedTrack>(node.Entity) is not { } track || !node.Rect.Contains(point)) continue;
            XsrUiSceneNode thumb = _scene.Nodes.FirstOrDefault(n => n.Entity == track.Thumb);
            if (!thumb.Entity.IsAssigned || !thumb.Rect.Contains(point)) return false;
            _segmentDrag = node.Entity; track.Dragging = true;
            Focus(track.Selected, showIndicator: false);
            track.GrabOffset = point.X - thumb.Rect.X; track.DragX = thumb.Rect.X;
            track.ScrollStartOffset = _tree.GetComponent<XsrUiScroll>(node.Entity)?.OffsetX ?? 0;
            track.ScrollThumbWidth = thumb.Rect.Width;
            track.ScrollStartProgress = Math.Clamp((thumb.Rect.X - node.Rect.X) / Math.Max(1, node.Rect.Width - thumb.Rect.Width), 0, 1);
            _tree.MarkDirty(node.Entity, XsrUiDirtyKinds.Paint);
            return true;
        }
        return false;
    }
    private bool MoveSegmentDrag(XsrUiPoint point)
    {
        if (!_segmentDrag.IsAssigned) return false;
        // Keep the active gesture's captured queries off ordinary pointer moves. A closure
        // in this method would otherwise be allocated before the early return too.
        return MoveActiveSegmentDrag(point);
    }
    private bool MoveActiveSegmentDrag(XsrUiPoint point)
    {
        if (!_tree.IsAlive(_segmentDrag) || !IsInVisibleTree(_segmentDrag)) return EndSegmentDrag();
        XsrUiSegmentedTrack track = _tree.GetComponent<XsrUiSegmentedTrack>(_segmentDrag)!;
        // Offscreen options participate in the catalog too. Scene hit-test flags deliberately
        // exclude clipped nodes, whereas this captured gesture moves the viewport itself.
        var segments = _tree.Children(_segmentDrag).Where(entity => IsInVisibleTree(entity)
            && _tree.GetComponent<XsrUiInput>(entity) is { Clickable: true } input && IsEnabled(input)
            && _paintRects.ContainsKey(entity.Index))
            .Select(entity => (Entity: entity, Rect: _paintRects[entity.Index])).ToArray();
        if (segments.Length == 0) return EndSegmentDrag();
        double left = segments.Min(n => n.Rect.X), right = segments.Max(n => n.Rect.X + n.Rect.Width);
        double scrollShift = 0;
        if (_tree.GetComponent<XsrUiScroll>(_segmentDrag) is { } scroll && _paintRects.TryGetValue(_segmentDrag.Index, out XsrUiRect viewport))
        {
            double overflow = Math.Max(0, _stackContentSizes.GetValueOrDefault(_segmentDrag.Index).Width - viewport.Width);
            double progress = (point.X - track.GrabOffset - viewport.X) / Math.Max(1, viewport.Width - track.ScrollThumbWidth);
            double target;
            if (progress >= track.ScrollStartProgress)
            {
                double traveled = (progress - track.ScrollStartProgress) / Math.Max(.001, 1 - track.ScrollStartProgress);
                target = track.ScrollStartOffset + (overflow - track.ScrollStartOffset) * Math.Clamp((traveled - .2) / .8, 0, 1);
            }
            else target = track.ScrollStartProgress > .001
                ? track.ScrollStartOffset * Math.Clamp(progress / track.ScrollStartProgress, 0, 1)
                : track.ScrollStartOffset + overflow * progress / .8;
            double offset = Math.Clamp(target, 0, overflow);
            scrollShift = offset - scroll.OffsetX;
            if (scrollShift != 0) { scroll.OffsetX = offset; _tree.MarkDirty(_segmentDrag, XsrUiDirtyKinds.Layout); }
        }
        track.DragX = Math.Clamp(point.X - track.GrabOffset, left - scrollShift, Math.Max(left - scrollShift, right - scrollShift - track.LastTarget.Width));
        var nearest = segments.MinBy(n => Math.Abs(point.X - (n.Rect.X - scrollShift) - n.Rect.Width / 2));
        if (nearest.Entity != track.Selected) Activate(nearest.Entity);
        _tree.MarkDirty(_segmentDrag, XsrUiDirtyKinds.Paint);
        return true;
    }
    private bool EndSegmentDrag()
    {
        if (!_segmentDrag.IsAssigned) return false;
        XsrUiEntityId entity = _segmentDrag; _segmentDrag = default;
        if (!_tree.IsAlive(entity)) return true;
        XsrUiSegmentedTrack track = _tree.GetComponent<XsrUiSegmentedTrack>(entity)!;
        track.Dragging = false;
        XsrUiTransition transition = _tree.GetComponent<XsrUiTransition>(track.Thumb)!;
        transition.OffsetX = track.DragX - track.LastTarget.X;
        transition.PresentedOffsetX = transition.OffsetX;
        transition.Key = $"segment-release-{++track.GestureRevision}";
        _tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
        return true;
    }
}
