using Nexa.Xsr.State;

namespace Nexa.UI.Next.Tests;

internal static partial class Program
{
    private static void LogicalFocusNavigationValidatesTargetsAndPreservesBarriers()
    {
        XsrUiTree tree = new();
        var root = tree.Create("root");
        tree.SetComponent(root, new XsrUiElement { Width = 300, Height = 200 });
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        var host = tree.Create("collection");
        tree.SetComponent(host, new XsrUiElement { Height = 100 });
        tree.SetComponent(host, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        tree.Attach(host, root);
        XsrUiEntityId Control(string name)
        {
            var entity = tree.Create(name);
            tree.SetComponent(entity, new XsrUiElement { Height = 20 });
            tree.SetComponent(entity, new XsrUiInput { Focusable = true });
            tree.Attach(entity, host);
            return entity;
        }
        var first = Control("first");
        var second = Control("second");
        var third = Control("third");
        XsrUiRenderer renderer = new(tree, new XsrStateStoreBuilder().Build());
        renderer.SetRoot(root);
        renderer.Render();
        int calls = 0;
        XsrUiEntityId target = third;
        bool direction = false;
        tree.SetComponent(host, new XsrUiFocusNavigation((origin, forward) =>
        {
            AssertEqual(first, origin);
            calls++; direction = forward;
            return target;
        }));
        AssertTrue(renderer.Focus(first));
        AssertTrue(renderer.FocusNext());
        AssertEqual(third, renderer.Focused);
        AssertTrue(direction);
        AssertTrue(renderer.Focus(first));
        AssertTrue(renderer.FocusPrevious());
        AssertEqual(third, renderer.Focused);
        AssertFalse(direction);

        foreach (int invalid in new[] { 0, 1, 2, 3 })
        {
            AssertTrue(renderer.Focus(first));
            target = invalid == 0 ? default : third;
            tree.GetComponent<XsrUiInput>(third)!.Enabled = invalid != 1;
            tree.GetComponent<XsrUiElement>(third)!.IsVisible = invalid != 2;
            if (invalid == 3) tree.Destroy(third);
            AssertTrue(renderer.FocusNext());
            AssertEqual(second, renderer.Focused);
        }
        AssertEqual(6, calls);
        AssertTrue(renderer.Focus(first));
        var modal = tree.Create("modal");
        tree.SetComponent(modal, new XsrUiElement { Height = 30 });
        tree.SetComponent(modal, new XsrUiInput { Focusable = true });
        tree.SetComponent(modal, new XsrUiOverlayLayer(isModal: true));
        tree.Attach(modal, root);
        AssertFalse(renderer.FocusNext());
        AssertEqual(6, calls);
        renderer.Render();
        AssertTrue(renderer.FocusNext());
        AssertEqual(modal, renderer.Focused);
        AssertEqual(6, calls);
    }
}
