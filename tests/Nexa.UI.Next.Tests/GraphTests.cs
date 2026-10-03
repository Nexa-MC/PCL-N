using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.UI.Next.Tests;

internal static partial class Program
{
    private static void GraphProjectionSizesDegreesAndSeparatesClickFromPan()
    {
        var graph = new XsrUiGraph([new(0, "shared", new(0, 90, 200)), new(1, "one", new()), new(2, "two", new())], [new(1, 0), new(2, 0)]);
        var projection = graph.Snapshot();
        AssertTrue(projection.Points.Single(n => n.Key == 0).Radius > projection.Points.Single(n => n.Key == 1).Radius);
        AssertEqual(new XsrUiGraphEdge(1, 0), projection.Edges[0]);
        AssertTrue(ReferenceEquals(projection, graph.Snapshot()));
        var tree = new XsrUiTree(); var canvas = tree.Create("canvas");
        tree.SetComponent(canvas, new XsrUiElement { Width = 300, Height = 200 });
        tree.SetComponent(canvas, graph); tree.SetComponent(canvas, new XsrUiInput { Clickable = true, Focusable = true });
        tree.SetComponent(canvas, new XsrUiCommandBinding(XsrSemanticId.Parse("test.graph")));
        var intents = new XsrUiIntentBuffer(); var renderer = new XsrUiRenderer(tree, new XsrStateStoreBuilder().Build(), intents);
        renderer.SetRoot(canvas); renderer.Render();
        AssertTrue(renderer.PointerPressed(new(150, 100))); AssertTrue(renderer.PointerReleased(new(150, 100)));
        AssertEqual<int?>(0, graph.Selected); AssertEqual(1, intents.Count);
        renderer.PointerPressed(new(150, 100)); renderer.PointerMoved(new(190, 130)); renderer.PointerReleased(new(190, 130));
        AssertClose(40, graph.PanX); AssertClose(30, graph.PanY); AssertEqual(1, intents.Count);
        AssertEqual<int?>(0, graph.HitTest(new(190, 130), new(0, 0, 300, 200)));
        graph.Zoom = 2;
        var other = graph.Snapshot().Points.Single(n => n.Key == 1);
        AssertEqual<int?>(1, graph.HitTest(new(190 + other.X * 2, 130 + other.Y * 2), new(0, 0, 300, 200)));
        AssertEqual<int?>(null, graph.HitTest(new(310, 100), new(0, 0, 300, 200)));
        AssertEqual(1d, projection.Zoom); // Published scene cannot observe later mutable view changes.
        renderer.PointerPressed(new(190, 130)); renderer.CancelPointerGesture(); AssertFalse(renderer.PointerReleased(new(190, 130)));
    }
}
