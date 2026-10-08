using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void VerifyNativeScrollbarPointerRouting(AvaloniaUiShellWindow window, XsrUiShell shell, AvaloniaUiSceneSurface surface)
    {
        XsrUiEntityId previous = shell.Stage.Navigation.Current;
        XsrUiEntityId page = shell.Tree.Create("native-scrollbar-page"), viewport = shell.Tree.Create("native-scrollbar-viewport"),
            text = shell.Tree.Create("native-scrollbar-text");
        XsrUiPager pager = new(XsrUiOrientation.Horizontal);
        shell.Tree.SetComponent(page, pager);
        shell.Tree.SetComponent(page, new XsrUiInput { Focusable = true });
        shell.Tree.SetComponent(viewport, new XsrUiElement { Width = 300, Height = 160 });
        shell.Tree.SetComponent(viewport, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiScroll scroll = new() { ShowsVerticalIndicator = true, ShowsHorizontalIndicator = true };
        shell.Tree.SetComponent(viewport, scroll);
        shell.Tree.SetComponent(viewport, new XsrUiScrollGesture());
        shell.Tree.SetComponent(viewport, new XsrUiInput { Clickable = true, Focusable = true });
        shell.Tree.SetComponent(viewport, new XsrUiCommandBinding(XsrSemanticId.Parse("test.scrollbar.click")));
        shell.Tree.SetComponent(viewport, new XsrUiFileDrag([Path.GetTempPath()], XsrUiFileDragEffects.Copy, default));
        shell.Tree.SetComponent(text, new XsrUiElement { Width = 1200, Height = 4000, HorizontalAlignment = XsrUiAlignment.Start });
        shell.Tree.SetComponent(text, new XsrUiInput { Focusable = true });
        shell.Tree.SetComponent(text, new XsrUiTextInput());
        shell.Tree.Attach(text, viewport);
        shell.Tree.Attach(viewport, page);
        int fileDrags = 0;
        var originalDragRunner = surface.FileDragRunner;
        surface.FileDragRunner = (_, _, _) => { fileDrags++; return Task.FromResult(DragDropEffects.None); };
        IPointer? pointer = null;
        void OnPress(object? sender, PointerPressedEventArgs args) => pointer = args.Pointer;
        // The surface handles scrollbar presses before ordinary routed handlers run. Observe
        // handled events too so capture assertions inspect the actual native pointer.
        surface.AddHandler(InputElement.PointerPressedEvent, OnPress, handledEventsToo: true);
        Point Native(XsrUiPoint point) => surface.TranslatePoint(new Point(point.X, point.Y), window)!.Value;
        XsrUiScrollbarSnapshot Bar(bool horizontal)
        {
            XsrUiSceneNode node = Node(surface.Scene!, viewport);
            return (horizontal ? node.HorizontalScrollbar : node.VerticalScrollbar)!.Value;
        }
        XsrUiPoint Thumb(bool horizontal)
        {
            XsrUiScrollbarSnapshot bar = Bar(horizontal);
            return horizontal ? new(bar.Thumb.X + bar.Thumb.Width / 2, bar.HitRect.Y + bar.HitRect.Height / 2)
                : new(bar.HitRect.X + bar.HitRect.Width / 2, bar.Thumb.Y + bar.Thumb.Height / 2);
        }
        try
        {
            shell.Stage.Navigation.Replace(page);
            surface.CommitScene(); window.UpdateLayout();
            AssertTrue(shell.Renderer.Focus(text));
            AssertTrue(shell.Renderer.SetTextInputValue(text, "Selected text"));
            AssertTrue(shell.Renderer.SetTextSelection(text, 0, 8));
            surface.CommitScene(); window.UpdateLayout();
            foreach (bool horizontal in new[] { false, true })
            {
                XsrUiScrollbarSnapshot bar = Bar(horizontal);
                XsrUiPoint point = Thumb(horizontal);
                AssertEqual(viewport, shell.Renderer.HitTest(point));
                window.MouseDown(Native(point), MouseButton.Left);
                AssertTrue(shell.Renderer.IsScrollbarGestureActive);
                AssertTrue(pointer?.Captured == surface);
                AssertFalse(pager.IsDragging);
                XsrUiPoint moved = horizontal ? point with { X = point.X + bar.Travel / 4, Y = point.Y - 60 }
                    : point with { X = point.X - 60, Y = point.Y + bar.Travel / 4 };
                window.MouseMove(Native(moved), RawInputModifiers.LeftMouseButton);
                double beforeWheel = horizontal ? scroll.OffsetX : scroll.OffsetY;
                AssertTrue(beforeWheel > 0);
                window.MouseWheel(Native(moved), horizontal ? new Vector(-1, 0) : new Vector(0, -1), RawInputModifiers.LeftMouseButton);
                double afterWheel = horizontal ? scroll.OffsetX : scroll.OffsetY;
                AssertTrue(afterWheel > beforeWheel);
                window.MouseMove(Native(moved), RawInputModifiers.LeftMouseButton);
                AssertEqual(afterWheel, horizontal ? scroll.OffsetX : scroll.OffsetY);
                window.MouseUp(Native(moved), MouseButton.Left);
                AssertFalse(shell.Renderer.IsScrollbarGestureActive);
                AssertTrue(pointer?.Captured is null);
                AssertEqual(text, shell.Renderer.Focused);
                AssertEqual("Selected", shell.Renderer.CopySelectedText()!);
                AssertFalse(shell.Tree.GetComponent<XsrUiInput>(viewport)!.IsPressed);
                AssertEqual(0d, pager.Position);

                bar = Bar(horizontal);
                XsrUiPoint track = horizontal ? new(bar.Track.X + bar.Track.Width - 1, bar.HitRect.Y + 6)
                    : new(bar.HitRect.X + 6, bar.Track.Y + bar.Track.Height - 1);
                XsrUiScrollSnapshot snapshot = Node(surface.Scene!, viewport).Scroll!.Value;
                window.MouseDown(Native(track), MouseButton.Left);
                window.MouseUp(Native(track), MouseButton.Left);
                AssertEqual(afterWheel + (horizontal ? snapshot.ViewportWidth : snapshot.ViewportHeight), horizontal ? scroll.OffsetX : scroll.OffsetY);
                AssertEqual("Selected", shell.Renderer.CopySelectedText()!);

                window.MouseDown(Native(Thumb(horizontal)), MouseButton.Left);
                AssertTrue(shell.Renderer.IsScrollbarGestureActive);
                pointer!.Capture(null); // Native capture loss cancels without a text/content release.
                AssertFalse(shell.Renderer.IsScrollbarGestureActive);
                window.MouseUp(Native(track), MouseButton.Left);
            }
            AssertEqual(0, fileDrags);
            window.MouseDown(Native(Thumb(false)), MouseButton.Left);
            shell.Stage.Navigation.Replace(previous);
            surface.CommitScene(); window.UpdateLayout();
            AssertFalse(shell.Renderer.IsScrollbarGestureActive);
            window.MouseUp(new Point(20, 20), MouseButton.Left);
            AssertEqual(0, fileDrags);
        }
        finally
        {
            pointer?.Capture(null);
            surface.RemoveHandler(InputElement.PointerPressedEvent, OnPress);
            surface.FileDragRunner = originalDragRunner;
            if (shell.Stage.Navigation.Current != previous) shell.Stage.Navigation.Replace(previous);
            surface.CommitScene();
            shell.Tree.Destroy(page);
        }
        Console.WriteLine("PASS: native scrollbar thumb/track input, capture loss, wheel and text/file-drag isolation");
    }
}
