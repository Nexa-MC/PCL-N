using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.UI.Next.Tests;

internal static partial class Program
{
    private static void SettledListEntriesDoNotReplayAfterUnrelatedFramesOrScrolling()
    {
        XsrUiTree tree = new();
        var root = tree.Create("root"); tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        var scroll = new XsrUiScroll(); tree.SetComponent(root, scroll);
        var first = tree.Create("first"); tree.Attach(first, root); tree.SetComponent(first, new XsrUiElement { Height = 80 });
        tree.SetComponent(first, new XsrUiText("first"));
        tree.SetComponent(first, new XsrUiTransition { Key = "page:1", MovesSelf = true, OffsetY = 6 });
        var tail = tree.Create("tail"); tree.Attach(tail, root); tree.SetComponent(tail, new XsrUiElement { Height = 250 });
        tree.SetComponent(tail, new XsrUiText("tail"));
        var renderer = new XsrUiRenderer(tree, new XsrStateStoreBuilder().Build()) { Viewport = new(200, 100) };
        renderer.SetRoot(root); renderer.Render();
        AssertEqual(0d, renderer.GetTransitionOffsetY(first));
        tree.GetComponent<XsrUiTransition>(first)!.Key = "page:entered";
        tree.MarkDirty(first, XsrUiDirtyKinds.Paint); renderer.Render();
        AssertEqual(6d, renderer.GetTransitionOffsetY(first));
        renderer.SetTransitionOffsetY(first, 0); renderer.Render();
        for (int index = 0; index < 5; index++)
        {
            tree.GetComponent<XsrUiText>(tail)!.Content = "tail " + index;
            tree.MarkDirty(tail, XsrUiDirtyKinds.Paint); renderer.Render();
            tree.MarkDirty(first, XsrUiDirtyKinds.Paint); renderer.Render();
            AssertEqual(0d, renderer.GetTransitionOffsetY(first));
        }
        scroll.OffsetY = 150; tree.MarkDirty(root, XsrUiDirtyKinds.Layout); renderer.Render();
        scroll.OffsetY = 0; tree.MarkDirty(root, XsrUiDirtyKinds.Layout); renderer.Render();
        AssertEqual(0d, renderer.GetTransitionOffsetY(first));
        tree.GetComponent<XsrUiTransition>(first)!.Key = "page:2";
        tree.MarkDirty(first, XsrUiDirtyKinds.Paint); renderer.Render();
        AssertEqual(6d, renderer.GetTransitionOffsetY(first));
        renderer.ReducedMotion = true; renderer.Render(); AssertEqual(0d, renderer.GetTransitionOffsetY(first));
    }

    private static void ContextMenusResolveParentsAndRetireCommands()
    {
        XsrUiTree tree = new(); XsrUiIntentBuffer intents = new();
        var root = tree.Create("root"); tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        var row = tree.Create("row"); tree.Attach(row, root); tree.SetComponent(row, new XsrUiElement { Height = 50 });
        tree.SetComponent(row, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        var label = tree.Create("label"); tree.Attach(label, row); tree.SetComponent(label, new XsrUiText("row"));
        var command = XsrSemanticId.Parse("ui.test.menu");
        tree.SetComponent(row, new XsrUiContextMenu([new("Open", command)]));
        var renderer = new XsrUiRenderer(tree, new XsrStateStoreBuilder().Build(), intents) { Viewport = new(200, 100) };
        renderer.SetRoot(root); renderer.Render();
        AssertEqual(row, renderer.ContextMenuFor(label)!.Source);
        AssertTrue(renderer.InvokeContextMenu(row, command)); AssertEqual(1, intents.Count);
        tree.SetComponent(row, new XsrUiContextMenu([new("Open", command, false)]));
        AssertTrue(!renderer.InvokeContextMenu(row, command)); AssertEqual(1, intents.Count);
        tree.SetComponent(row, new XsrUiContextMenu([new("Open", command)]));
        tree.SetComponent(root, new XsrUiElement { IsVisible = false });
        AssertTrue(!renderer.InvokeContextMenu(row, command));
        tree.SetComponent(root, new XsrUiElement { IsVisible = true });
        tree.SetComponent(root, new XsrUiContextMenu([new("Global", command)]));
        var modal = tree.Create("modal"); tree.Attach(modal, root);
        tree.SetComponent(modal, new XsrUiOverlayLayer(isModal: true));
        renderer.Render();
        AssertTrue(renderer.ContextMenuFor(modal) is null);
        AssertTrue(!renderer.InvokeContextMenu(root, command));
        AssertTrue(!renderer.InvokeContextMenu(row, command));
        tree.Destroy(modal);
        tree.Detach(row); AssertTrue(!renderer.InvokeContextMenu(row, command));
        tree.Destroy(row); AssertTrue(!renderer.InvokeContextMenu(row, command));
    }
}
