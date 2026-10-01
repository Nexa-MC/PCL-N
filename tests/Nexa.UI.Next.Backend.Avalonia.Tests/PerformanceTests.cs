using Avalonia;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void UnchangedNodesSkipApplyAndRetainClipGeometry()
    {
        var tree = new XsrUiTree();
        var entity = tree.Create("stable");
        var node = new XsrUiSceneNode(entity, new(10, 20, 100, 40), 0,
            XsrUiSemanticRole.Text, "Stable", "content", null, false, null, null,
            ClipRect: new(10, 20, 100, 40));
        var control = new AvaloniaUiSceneNodeControl(_ => { }, _ => { }, () => true);
        control.Apply(node);
        var clip = control.Clip;
        for (int index = 0; index < 1000; index++) control.Apply(node);
        AssertEqual(1, control.AppliedChanges);
        AssertTrue(ReferenceEquals(clip, control.Clip));
        control.Apply(node with { Text = "changed" });
        AssertEqual(2, control.AppliedChanges);
        AssertTrue(ReferenceEquals(clip, control.Clip));
        control.Apply(node with { Rect = new(11, 20, 100, 40), Text = "changed" });
        AssertTrue(!ReferenceEquals(clip, control.Clip));
        control.ReleasePresentation();
    }
}
