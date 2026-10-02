using Nexa.Xsr.State;

namespace Nexa.UI.Next.Tests;

internal static partial class Program
{
    private static void SteadyPointerQueriesAllocateNoManagedMemory()
    {
        XsrUiTree tree = new();
        var root = tree.Create("root");
        tree.SetComponent(root, new XsrUiElement { Width = 300, Height = 200 });
        var panel = tree.Create("panel");
        tree.SetComponent(panel, new XsrUiElement { Width = 200, Height = 100 });
        tree.Attach(panel, root);
        for (int index = 0; index < 12; index++)
        {
            var decoration = tree.Create("decoration");
            tree.SetComponent(decoration, new XsrUiElement { Width = 200, Height = 100 });
            tree.Attach(decoration, panel);
        }
        var button = tree.Create("button");
        tree.SetComponent(button, new XsrUiElement { Width = 120, Height = 40 });
        tree.SetComponent(button, new XsrUiInput { Clickable = true });
        tree.SetComponent(button, new XsrUiVisualStyle { HoverExpand = true });
        tree.Attach(button, panel);
        var label = tree.Create("label");
        tree.SetComponent(label, new XsrUiElement { Width = 40, Height = 40 });
        tree.Attach(label, button);
        XsrUiRenderer renderer = new(tree, new XsrStateStoreBuilder().Build());
        renderer.SetRoot(root);
        renderer.Render();
        for (int index = 0; index < 200_000; index++)
        {
            XsrUiPoint point = new(20 + (index & 1), 20);
            renderer.PointerMoved(point);
            _ = renderer.PointerCursorAt(point);
        }
        renderer.Render();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int handCount = 0, changedCount = 0;
        for (int index = 0; index < 10_000; index++)
        {
            XsrUiPoint point = new(20 + (index & 1), 20);
            if (renderer.PointerMoved(point)) changedCount++;
            if (renderer.PointerCursorAt(point) == XsrUiPointerCursor.Hand) handCount++;
        }
        long hoverBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        AssertEqual(10_000, handCount);
        AssertEqual(0, changedCount);
        AssertFalse(tree.HasDirtySubtree(root));

        // A tiny edge move retains the capsule's hover while hit geometry resolves background.
        var bounds = renderer.Render().Nodes.Single(node => node.Entity == button).Rect;
        renderer.PointerMoved(new(bounds.X + bounds.Width - 1, bounds.Y + 20));
        XsrUiPoint edge = new(bounds.X + bounds.Width + 1, bounds.Y + 20);
        for (int index = 0; index < 20_000; index++)
        {
            renderer.PointerMoved(edge);
            _ = renderer.PointerCursorAt(edge);
        }
        renderer.Render();
        before = GC.GetAllocatedBytesForCurrentThread();
        handCount = 0;
        changedCount = 0;
        for (int index = 0; index < 10_000; index++)
        {
            if (renderer.PointerMoved(edge)) changedCount++;
            if (renderer.PointerCursorAt(edge) == XsrUiPointerCursor.Hand) handCount++;
        }
        long edgeBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        AssertEqual(10_000, handCount);
        AssertEqual(0, changedCount);
        AssertFalse(tree.HasDirtySubtree(root));

        XsrUiPoint background = new(150, 90);
        for (int index = 0; index < 10_000; index++)
        {
            renderer.PointerMoved(background);
            _ = renderer.PointerCursorAt(background);
        }
        renderer.Render();
        before = GC.GetAllocatedBytesForCurrentThread();
        int defaultCount = 0;
        changedCount = 0;
        for (int index = 0; index < 1_000; index++)
        {
            if (renderer.PointerMoved(background)) changedCount++;
            if (renderer.PointerCursorAt(background) == XsrUiPointerCursor.Default) defaultCount++;
        }
        long backgroundBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        AssertEqual(1_000, defaultCount);
        AssertEqual(0, changedCount);
        AssertFalse(tree.HasDirtySubtree(root));
        if (hoverBytes != 0 || edgeBytes != 0 || backgroundBytes != 0)
            throw new InvalidOperationException($"Steady pointer queries allocated hover={hoverBytes}, edge={edgeBytes}, background={backgroundBytes} bytes.");

        // The next scene retires the old ancestor indexes; scratch must not retain hit decisions.
        tree.Destroy(panel);
        var replacement = tree.Create("replacement");
        tree.SetComponent(replacement, new XsrUiElement { Width = 100, Height = 40 });
        tree.SetComponent(replacement, new XsrUiInput { Clickable = true });
        tree.Attach(replacement, root);
        renderer.Render();
        AssertTrue(renderer.PointerMoved(new(20, 20)));
        AssertEqual(XsrUiPointerCursor.Hand, renderer.PointerCursorAt(new(20, 20)));
        AssertFalse(tree.IsAlive(button));
    }

    private static void StructuralInputBarriersFollowVisibleSiblingOrder()
    {
        XsrUiTree tree = new();
        var root = tree.Create("root");
        tree.SetComponent(root, new XsrUiElement { Width = 300, Height = 200 });
        var pager = tree.Create("pager");
        tree.SetComponent(pager, new XsrUiElement { Width = 300, Height = 100 });
        var paging = new XsrUiPager();
        tree.SetComponent(pager, paging);
        tree.Attach(pager, root);
        XsrUiEntityId Page(string name, bool visible)
        {
            var page = tree.Create(name);
            tree.SetComponent(page, new XsrUiElement { Height = 100, IsVisible = visible });
            tree.SetComponent(page, new XsrUiInput { Focusable = true });
            tree.Attach(page, pager);
            return page;
        }
        var hidden = Page("hidden", false);
        var first = Page("first", true);
        var second = Page("second", true);
        XsrUiRenderer renderer = new(tree, new XsrStateStoreBuilder().Build());
        renderer.SetRoot(root);
        renderer.Render();
        AssertFalse(renderer.Focus(hidden));
        AssertTrue(renderer.Focus(first));
        AssertFalse(renderer.Focus(second));
        AssertTrue(renderer.SelectPagerPage(pager, 1));
        AssertFalse(renderer.Focus(first));
        AssertTrue(renderer.Focus(second));

        var modal = tree.Create("modal");
        tree.SetComponent(modal, new XsrUiElement { Height = 30 });
        tree.SetComponent(modal, new XsrUiInput { Focusable = true });
        tree.SetComponent(modal, new XsrUiOverlayLayer(isModal: true));
        tree.Attach(modal, root);
        // A staged modal is already a barrier before its first immutable scene.
        AssertFalse(renderer.Focus(second));
        AssertTrue(renderer.Focus(modal));
        var top = tree.Create("top-modal");
        tree.SetComponent(top, new XsrUiElement { Height = 30 });
        tree.SetComponent(top, new XsrUiInput { Focusable = true });
        tree.SetComponent(top, new XsrUiOverlayLayer(isModal: true));
        tree.Attach(top, root);
        AssertFalse(renderer.Focus(modal));
        AssertTrue(renderer.Focus(top));
        tree.GetComponent<XsrUiElement>(top)!.IsVisible = false;
        AssertTrue(renderer.Focus(modal));
        tree.GetComponent<XsrUiElement>(modal)!.IsVisible = false;
        AssertTrue(renderer.Focus(second));
    }
}
