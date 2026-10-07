using Nexa.Xsr.State;

namespace Nexa.UI.Next.Tests;

internal static partial class Program
{
    private static void HorizontalCardWheelPreservesAxesAndBubblesAtBoundaries()
    {
        XsrUiTree tree = new();
        XsrStateStore store = new XsrStateStoreBuilder().Build();
        var root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiScroll outer = new(); tree.SetComponent(root, outer);
        var track = tree.Create("track"); tree.Attach(track, root);
        tree.SetComponent(track, new XsrUiElement { Height = 50 });
        tree.SetComponent(track, new XsrUiStackPanel(XsrUiOrientation.Horizontal));
        XsrUiScroll horizontal = new() { UseVerticalWheelForHorizontalScroll = true };
        tree.SetComponent(track, horizontal);
        for (int i = 0; i < 3; i++)
        {
            var card = tree.Create("card" + i); tree.Attach(card, track);
            tree.SetComponent(card, new XsrUiElement { Width = 80, Height = 50 });
        }
        var tail = tree.Create("tail"); tree.Attach(tail, root);
        tree.SetComponent(tail, new XsrUiElement { Height = 100 });
        var renderer = new XsrUiRenderer(tree, store, new XsrUiIntentBuffer()) { Viewport = new(100, 100) };
        renderer.SetRoot(root); renderer.Render();

        AssertTrue(renderer.PointerScroll(new(20, 20), 50)); renderer.Render();
        AssertEqual(50d, horizontal.OffsetX); AssertEqual(0d, outer.OffsetY);
        AssertTrue(renderer.PointerScroll(new(20, 20), 40, 20)); renderer.Render();
        AssertEqual(70d, horizontal.OffsetX); AssertEqual(0d, horizontal.OffsetY);
        AssertTrue(renderer.PointerScroll(new(20, 20), 1000)); renderer.Render();
        AssertEqual(140d, horizontal.OffsetX);
        AssertTrue(renderer.PointerScroll(new(20, 20), 20)); renderer.Render();
        AssertEqual(140d, horizontal.OffsetX); AssertEqual(20d, outer.OffsetY);
        AssertTrue(renderer.PointerScroll(new(20, 20), -30)); renderer.Render();
        AssertEqual(110d, horizontal.OffsetX); AssertEqual(20d, outer.OffsetY);
    }
}
