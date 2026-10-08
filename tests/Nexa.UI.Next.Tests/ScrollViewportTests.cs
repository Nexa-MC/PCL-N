using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.UI.Next.Tests;

internal static partial class Program
{
    private static (XsrUiTree Tree, XsrUiRenderer Renderer, XsrUiEntityId Root, XsrUiEntityId Child, XsrUiScroll Scroll)
        ScrollViewport(XsrUiOrientation direction, bool wraps = false)
    {
        XsrUiTree tree = new();
        XsrUiEntityId root = tree.Create("scroll"), child = tree.Create("content");
        tree.SetComponent(root, new XsrUiElement { Width = 300, Height = 40 });
        tree.SetComponent(root, new XsrUiStackPanel(direction));
        XsrUiScroll scroll = new() { ShowsVerticalIndicator = true };
        tree.SetComponent(root, scroll);
        tree.SetComponent(child, new XsrUiElement
        {
            Weight = direction == XsrUiOrientation.Horizontal ? 1 : 0,
            Height = !wraps && direction == XsrUiOrientation.Vertical ? 20 : null
        });
        if (wraps)
        {
            tree.SetComponent(child, new XsrUiText(new string('x', 84)) { MaxLines = 10 });
            tree.SetComponent(child, new XsrUiVisualStyle { FontSize = 14, WrapText = true });
        }
        tree.Attach(child, root);
        XsrUiRenderer renderer = new(tree, new XsrStateStoreBuilder().Build());
        renderer.SetRoot(root);
        return (tree, renderer, root, child, scroll);
    }

    private static void VerticalStackReservesVerticalIndicatorGutter()
    {
        var f = ScrollViewport(XsrUiOrientation.Vertical);
        XsrUiScene scene = f.Renderer.Render();
        AssertEqual(300d, scene.Nodes.Single(n => n.Entity == f.Root).Rect.Width);
        AssertEqual(288d, scene.Nodes.Single(n => n.Entity == f.Child).Rect.Width);
    }

    private static void HorizontalStackReservesRightSideVerticalIndicatorGutter()
    {
        var f = ScrollViewport(XsrUiOrientation.Horizontal);
        XsrUiScene scene = f.Renderer.Render();
        AssertEqual(new XsrUiRect(0, 0, 288, 40), scene.Nodes.Single(n => n.Entity == f.Child).Rect);
    }

    private static void WrappedContentMeasuresInsideIndicatorViewport()
    {
        var f = ScrollViewport(XsrUiOrientation.Vertical, wraps: true);
        XsrUiScene scene = f.Renderer.Render();
        AssertEqual(new XsrUiRect(0, 0, 288, 60), scene.Nodes.Single(n => n.Entity == f.Child).Rect);
    }

    private static void IndicatorGutterAffectsScrollExtentCoherently()
    {
        var f = ScrollViewport(XsrUiOrientation.Vertical, wraps: true);
        f.Scroll.OffsetY = 100;
        XsrUiScene scene = f.Renderer.Render();
        AssertEqual(20d, f.Scroll.OffsetY);
        var snapshot = scene.Nodes.Single(n => n.Entity == f.Root).Scroll!.Value;
        AssertEqual(288d, snapshot.ViewportWidth);
        AssertEqual(60d, snapshot.ContentHeight);
        AssertEqual(20d, snapshot.MaximumOffsetY);
        XsrUiRect child = scene.Nodes.Single(n => n.Entity == f.Child).Rect;
        AssertEqual(40d, child.Y + child.Height);
        f.Scroll.ShowsVerticalIndicator = false;
        f.Tree.MarkDirty(f.Root, XsrUiDirtyKinds.Layout);
        scene = f.Renderer.Render();
        AssertEqual(0d, f.Scroll.OffsetY);
        AssertEqual(new XsrUiRect(0, 0, 300, 40), scene.Nodes.Single(n => n.Entity == f.Child).Rect);
    }

    private static void ScrollSnapshotQueryUsesLastSceneAndRejectsRetiredEntities()
    {
        var f = ScrollViewport(XsrUiOrientation.Vertical);
        f.Tree.GetComponent<XsrUiElement>(f.Child)!.Height = 120;
        AssertFalse(f.Renderer.TryGetScrollSnapshot(f.Root, out _));
        XsrUiScene scene = f.Renderer.Render();
        AssertTrue(f.Renderer.TryGetScrollSnapshot(f.Root, out var initial));
        AssertEqual(scene.Nodes.Single(node => node.Entity == f.Root).Scroll!.Value, initial);
        AssertFalse(f.Renderer.TryGetScrollSnapshot(f.Child, out _));
        f.Scroll.OffsetY = 30;
        f.Tree.MarkDirty(f.Root, XsrUiDirtyKinds.Layout);
        AssertTrue(f.Renderer.TryGetScrollSnapshot(f.Root, out var pending));
        AssertEqual(initial, pending);
        f.Renderer.Render();
        AssertTrue(f.Renderer.TryGetScrollSnapshot(f.Root, out var current));
        AssertEqual(30d, current.OffsetY);
        f.Tree.SetComponent<XsrUiScroll>(f.Root, null);
        AssertFalse(f.Renderer.TryGetScrollSnapshot(f.Root, out _));
        f.Tree.Destroy(f.Root);
        AssertFalse(f.Renderer.TryGetScrollSnapshot(f.Root, out _));
    }

    private static (XsrUiTree Tree, XsrUiRenderer Renderer, XsrUiEntityId Root,
        XsrUiEntityId Content, XsrUiScroll Scroll, XsrUiIntentBuffer Intents) ScrollbarViewport(bool horizontal = false)
    {
        XsrUiTree tree = new();
        XsrUiEntityId root = tree.Create("scrollbar"), content = tree.Create("scrollbar-content");
        tree.SetComponent(root, new XsrUiElement { Width = 300, Height = 120 });
        tree.SetComponent(root, new XsrUiStackPanel(horizontal ? XsrUiOrientation.Horizontal : XsrUiOrientation.Vertical));
        XsrUiScroll scroll = new() { ShowsVerticalIndicator = !horizontal, ShowsHorizontalIndicator = horizontal };
        tree.SetComponent(root, scroll);
        tree.SetComponent(root, new XsrUiScrollGesture());
        tree.SetComponent(root, new XsrUiInput { Clickable = true, Focusable = true });
        tree.SetComponent(root, new XsrUiCommandBinding("test.scroll-content".AsXsrId()));
        tree.SetComponent(root, new XsrUiFileDrag([Path.GetTempPath()], XsrUiFileDragEffects.Copy, default));
        tree.SetComponent(content, new XsrUiElement { Width = horizontal ? 10000 : null, Height = horizontal ? null : 10000 });
        tree.Attach(content, root);
        XsrUiIntentBuffer intents = new();
        XsrUiRenderer renderer = new(tree, new XsrStateStoreBuilder().Build(), intents) { Viewport = new(300, 120) };
        renderer.SetRoot(root);
        return (tree, renderer, root, content, scroll, intents);
    }

    private static XsrUiPoint ScrollbarThumbPoint(XsrUiScrollbarSnapshot bar) => bar.Direction == XsrUiOrientation.Vertical
        ? new(bar.HitRect.X + bar.HitRect.Width / 2, bar.Thumb.Y + bar.Thumb.Height / 2)
        : new(bar.Thumb.X + bar.Thumb.Width / 2, bar.HitRect.Y + bar.HitRect.Height / 2);

    private static void ScrollbarThumbDragAndTrackPageUseSceneGeometry()
    {
        foreach (bool horizontal in new[] { false, true })
        {
            var f = ScrollbarViewport(horizontal);
            XsrUiSceneNode node = f.Renderer.Render().Nodes.Single(n => n.Entity == f.Root);
            XsrUiScrollbarSnapshot bar = (horizontal ? node.HorizontalScrollbar : node.VerticalScrollbar)!.Value;
            AssertEqual(28d, horizontal ? bar.Thumb.Width : bar.Thumb.Height);
            XsrUiPoint point = ScrollbarThumbPoint(bar);
            AssertEqual(XsrUiPointerCursor.Default, f.Renderer.PointerCursorAt(point));
            AssertFalse(f.Renderer.FileDragTarget(point).IsAssigned);
            AssertTrue(f.Renderer.PointerPressed(point));
            AssertTrue(f.Renderer.IsScrollbarGestureActive);
            XsrUiPoint end = horizontal ? point with { X = point.X + bar.Travel } : point with { Y = point.Y + bar.Travel };
            AssertTrue(f.Renderer.PointerMoved(end));
            AssertClose(horizontal ? node.Scroll!.Value.MaximumOffsetX : node.Scroll!.Value.MaximumOffsetY,
                horizontal ? f.Scroll.OffsetX : f.Scroll.OffsetY);
            AssertTrue(f.Renderer.PointerReleased(new(-100, -100)));
            AssertFalse(f.Renderer.IsScrollbarGestureActive);
            AssertEqual(0d, f.Renderer.Render().Nodes.Single(n => n.Entity == f.Root).ScrollMotion!.Value.Velocity);

            node = f.Renderer.Render().Nodes.Single(n => n.Entity == f.Root);
            bar = (horizontal ? node.HorizontalScrollbar : node.VerticalScrollbar)!.Value;
            point = horizontal ? new(bar.Track.X + 1, bar.HitRect.Y + 6) : new(bar.HitRect.X + 6, bar.Track.Y + 1);
            double maximum = horizontal ? node.Scroll!.Value.MaximumOffsetX : node.Scroll!.Value.MaximumOffsetY;
            double viewport = horizontal ? node.Scroll!.Value.ViewportWidth : node.Scroll!.Value.ViewportHeight;
            AssertTrue(f.Renderer.PointerPressed(point));
            AssertClose(maximum - viewport, horizontal ? f.Scroll.OffsetX : f.Scroll.OffsetY);
            AssertTrue(f.Renderer.PointerMoved(new(20, 20))); // Track ownership does not become a content drag.
            AssertTrue(f.Renderer.PointerReleased(new(20, 20)));
            f.Renderer.Render();
            AssertTrue(f.Renderer.PointerScroll(point, horizontal ? 0 : -20, horizontal ? -20 : 0));
            AssertClose(maximum - viewport - 20, horizontal ? f.Scroll.OffsetX : f.Scroll.OffsetY);
            AssertEqual(0, f.Intents.Count);
        }
    }

    private static void ScrollbarDragSharesWheelOffsetAndAdaptsToResize()
    {
        var f = ScrollbarViewport();
        XsrUiSceneNode node = f.Renderer.Render().Nodes.Single(n => n.Entity == f.Root);
        XsrUiPoint point = ScrollbarThumbPoint(node.VerticalScrollbar!.Value);
        AssertTrue(f.Renderer.PointerPressed(point));
        point = point with { Y = point.Y + 10 };
        AssertTrue(f.Renderer.PointerMoved(point));
        f.Renderer.Render();
        AssertTrue(f.Renderer.PointerScroll(new(294, 70), 50));
        double afterWheel = f.Scroll.OffsetY;
        f.Renderer.Render();
        AssertTrue(f.Renderer.PointerMoved(point));
        AssertClose(afterWheel, f.Scroll.OffsetY);
        f.Tree.GetComponent<XsrUiElement>(f.Root)!.Height = 180;
        f.Renderer.Viewport = new(300, 180);
        f.Tree.MarkDirty(f.Root, XsrUiDirtyKinds.Layout);
        node = f.Renderer.Render().Nodes.Single(n => n.Entity == f.Root);
        double delta = node.VerticalScrollbar!.Value.Travel / 10;
        point = point with { Y = point.Y + delta };
        AssertTrue(f.Renderer.PointerMoved(point));
        AssertClose(afterWheel + node.Scroll!.Value.MaximumOffsetY / 10, f.Scroll.OffsetY);
        AssertTrue(f.Renderer.CancelPointerGesture());
        AssertFalse(f.Renderer.PointerReleased(point));
        AssertFalse(f.Renderer.IsScrollbarGestureActive);

        node = f.Renderer.Render().Nodes.Single(n => n.Entity == f.Root);
        AssertTrue(f.Renderer.PointerPressed(ScrollbarThumbPoint(node.VerticalScrollbar!.Value)));
        f.Tree.GetComponent<XsrUiElement>(f.Content)!.Height = 20;
        f.Tree.MarkDirty(f.Content, XsrUiDirtyKinds.Layout);
        node = f.Renderer.Render().Nodes.Single(n => n.Entity == f.Root);
        AssertEqual(0d, f.Scroll.OffsetY);
        AssertTrue(node.VerticalScrollbar is null);
        AssertFalse(f.Renderer.IsScrollbarGestureActive);
        AssertFalse(f.Renderer.PointerReleased(point));
        AssertEqual(0, f.Intents.Count);
    }

    private static void ScrollbarOwnershipRejectsLiveBarriersAndRetiredPages()
    {
        for (int mutation = 0; mutation < 7; mutation++)
        {
            var f = ScrollbarViewport();
            XsrUiSceneNode node = f.Renderer.Render().Nodes.Single(n => n.Entity == f.Root);
            XsrUiPoint point = ScrollbarThumbPoint(node.VerticalScrollbar!.Value);
            AssertTrue(f.Renderer.PointerPressed(point));
            switch (mutation)
            {
                case 0: f.Tree.GetComponent<XsrUiInput>(f.Root)!.Enabled = false; break;
                case 1: f.Tree.GetComponent<XsrUiElement>(f.Root)!.IsVisible = false; break;
                case 2: f.Scroll.ShowsVerticalIndicator = false; break;
                case 3: f.Tree.SetComponent<XsrUiScroll>(f.Root, null); break;
                case 4:
                    var replacement = f.Tree.Create("new-page");
                    f.Renderer.SetRoot(replacement); break;
                case 5:
                    var modal = f.Tree.Create("staged-modal");
                    f.Tree.SetComponent(modal, new XsrUiOverlayLayer(isModal: true));
                    f.Tree.Attach(modal, f.Root); break;
                case 6: f.Tree.Destroy(f.Root); break;
            }
            AssertTrue(f.Renderer.PointerMoved(point with { Y = point.Y + 30 }));
            AssertEqual(0d, f.Scroll.OffsetY);
            AssertFalse(f.Renderer.IsScrollbarGestureActive);
            AssertFalse(f.Renderer.PointerReleased(point));
            AssertEqual(0, f.Intents.Count);
        }
        var disabled = ScrollbarViewport();
        XsrUiPoint oldScenePoint = ScrollbarThumbPoint(disabled.Renderer.Render().Nodes.Single(n => n.Entity == disabled.Root).VerticalScrollbar!.Value);
        disabled.Tree.GetComponent<XsrUiInput>(disabled.Root)!.Enabled = false;
        AssertFalse(disabled.Renderer.PointerPressed(oldScenePoint));
        AssertFalse(disabled.Renderer.PointerScroll(oldScenePoint, 50));
    }

    private static void ScrollbarsRespectNestedClipsOcclusionAndPagerOwnership()
    {
        var f = ScrollbarViewport();
        XsrUiEntityId outer = f.Tree.Create("outer-scroll"), pagerEntity = f.Tree.Create("drag-pager");
        f.Tree.SetComponent(outer, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiScroll outerScroll = new() { ShowsVerticalIndicator = true };
        f.Tree.SetComponent(outer, outerScroll);
        f.Tree.GetComponent<XsrUiElement>(f.Root)!.Width = 150;
        f.Tree.GetComponent<XsrUiElement>(f.Root)!.Height = 240;
        f.Tree.Attach(f.Root, outer);
        XsrUiPager pager = new(XsrUiOrientation.Horizontal);
        f.Tree.SetComponent(pagerEntity, pager);
        f.Tree.Attach(outer, pagerEntity);
        f.Renderer.SetRoot(pagerEntity);
        XsrUiSceneNode inner = f.Renderer.Render().Nodes.Single(n => n.Entity == f.Root);
        XsrUiScrollbarSnapshot bar = inner.VerticalScrollbar!.Value;
        AssertFalse(f.Renderer.PointerPressed(new(bar.HitRect.X + 6, 140)));
        XsrUiPoint point = ScrollbarThumbPoint(bar);
        AssertTrue(f.Renderer.PointerPressed(point));
        AssertFalse(pager.IsDragging);
        AssertTrue(f.Renderer.PointerMoved(point with { Y = point.Y + bar.Travel }));
        AssertTrue(f.Renderer.PointerReleased(point));
        AssertTrue(f.Scroll.OffsetY > 0);
        AssertEqual(0d, outerScroll.OffsetY);
        AssertEqual(0d, pager.Position);
        var overlay = f.Tree.Create("overlapping-content");
        f.Tree.SetComponent(overlay, new XsrUiOverlayLayer(isModal: false));
        f.Tree.Attach(overlay, outer);
        f.Renderer.Render();
        // The later overlay covers the inner bar, so a press cannot start scrollbar ownership.
        f.Renderer.PointerPressed(point);
        AssertFalse(f.Renderer.IsScrollbarGestureActive);
        f.Renderer.CancelPointerGesture();
        AssertEqual(0, f.Intents.Count);
    }

    private static void BothScrollbarGuttersAndTinyThumbsStayWithinBounds()
    {
        var f = ScrollbarViewport();
        f.Scroll.ShowsHorizontalIndicator = true;
        f.Tree.GetComponent<XsrUiElement>(f.Content)!.Width = 1200;
        f.Tree.GetComponent<XsrUiElement>(f.Content)!.HorizontalAlignment = XsrUiAlignment.Start;
        XsrUiSceneNode node = f.Renderer.Render().Nodes.Single(n => n.Entity == f.Root);
        AssertEqual(288d, node.Scroll!.Value.ViewportWidth);
        AssertEqual(108d, node.Scroll!.Value.ViewportHeight);
        AssertTrue(node.VerticalScrollbar!.Value.Track.Y + node.VerticalScrollbar.Value.Track.Height <= 108);
        AssertTrue(node.HorizontalScrollbar!.Value.Track.X + node.HorizontalScrollbar.Value.Track.Width <= 288);
        XsrUiPoint corner = new(294, 114);
        f.Renderer.PointerPressed(corner);
        AssertFalse(f.Renderer.IsScrollbarGestureActive);
        f.Renderer.CancelPointerGesture();
        f.Scroll.ShowsHorizontalIndicator = false;
        f.Tree.GetComponent<XsrUiElement>(f.Root)!.Height = 24;
        f.Tree.MarkDirty(f.Root, XsrUiDirtyKinds.Layout);
        node = f.Renderer.Render().Nodes.Single(n => n.Entity == f.Root);
        XsrUiScrollbarSnapshot bar = node.VerticalScrollbar!.Value;
        AssertTrue(bar.Thumb.Height <= bar.Track.Height);
        AssertEqual(0d, bar.Travel);
        AssertFalse(f.Renderer.PointerPressed(ScrollbarThumbPoint(bar)));
        AssertFalse(f.Renderer.IsScrollbarGestureActive);
        AssertEqual(0d, f.Scroll.OffsetY);
    }
}
