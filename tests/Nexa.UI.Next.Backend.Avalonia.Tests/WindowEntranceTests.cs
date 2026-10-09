using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static bool _windowEntranceVerified;

    private static void WindowEntrancePreservesPreparedInputAndRetiresMotion() => AssertTrue(_windowEntranceVerified);

    // Run inside the suite's existing native dispatcher/lifetime, with independent prepared
    // secondary windows. The registered case checks that this asynchronous scenario finished.
    private static async Task VerifyPreparedWindowEntranceAsync(AvaloniaUiShellWindow owner)
    {
        _windowEntranceVerified = false;
        var shell = XsrUiShellComposer.Compose(new XsrStateStoreBuilder().Build());
        shell.Renderer.ReducedMotion = false;
        var window = new AvaloniaUiShellWindow(shell);
        int completed = 0;
        window.StartupRevealCompleted += (_, _) => completed++;
        try
        {
            window.PrepareStartupScene();
            AssertFalse(window.IsVisible);
            AssertTrue(window.Surface.Scene is { Count: > 0 });
            var preparedControls = window.Surface.Children.ToArray();
            window.Show();
            var trace = window.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "NexaWindowEntrance");
            AssertFalse(trace.IsHitTestVisible);
            AssertEqual(0, completed);
            AssertTrue(preparedControls.All(control => window.Surface.Children.Contains(control)));
            AssertTrue(window.Surface.Scene is { Count: > 0 });
            AssertEqual(1d, window.Opacity);
            var root = (Grid)window.Content!;
            var sceneContainer = (Grid)root.Children[0];
            AssertTrue(sceneContainer.Clip is null && sceneContainer.RenderTransform is null);
            AssertEqual(.88, sceneContainer.Opacity);
            Size nativeSize = window.Bounds.Size;

            // Actual native pointer routing remains available while the decoration is present.
            var settings = window.Surface.Children.OfType<AvaloniaUiSceneNodeControl>().Single(control =>
                control.Node.Role == XsrUiSemanticRole.NavigationItem && control.Node.Label == "设置");
            var bounds = settings.Node.Rect;
            Point point = window.Surface.TranslatePoint(new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            window.Surface.CommitScene();
            AssertEqual(XsrSemanticId.Parse("navigation.settings"), shell.SelectedNavigationId);
            AssertEqual(nativeSize, window.Bounds.Size);
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            AssertTrue(window.FocusManager!.GetFocusedElement() is AvaloniaUiSceneNodeControl);

            // Resize updates the actual scene, rather than retargeting an obsolete entrance mask.
            window.Width += 64; window.Height += 32;
            Size resizedNativeSize = new(window.Width, window.Height);
            // Native layout and its posted scene commit are independent of the entrance timer.
            // Wait for the requested native size and the resized renderer viewport explicitly.
            await WaitAsync(() =>
            {
                window.UpdateLayout();
                window.Surface.CommitScene();
                return window.Bounds.Size == resizedNativeSize
                    && window.Surface.Bounds.Width > 850 && window.Surface.Bounds.Height > 500
                    && shell.Renderer.Viewport == new XsrUiSize(window.Surface.Bounds.Width, window.Surface.Bounds.Height);
            });
            await WaitAsync(() => !HasTrace(window));
            AssertEqual(1, completed);
            AssertTrue(sceneContainer.Clip is null && sceneContainer.RenderTransform is null);
            AssertEqual(1d, sceneContainer.Opacity);
            AssertEqual(1d, window.Opacity);
            AssertEqual(resizedNativeSize, window.Bounds.Size);
            AssertTrue(window.Surface.Bounds.Width > 850 && window.Surface.Bounds.Height > 500);
            AssertEqual(new XsrUiSize(window.Surface.Bounds.Width, window.Surface.Bounds.Height), shell.Renderer.Viewport);
            window.Hide(); window.Show(); window.Activate(); window.Surface.CommitScene();
            AssertFalse(HasTrace(window)); AssertEqual(1, completed);
        }
        finally { shell.Renderer.ReducedMotion = true; window.RequestClose(); }

        var reduced = XsrUiShellComposer.Compose(new XsrStateStoreBuilder().Build());
        reduced.Renderer.ReducedMotion = true;
        var reducedWindow = new AvaloniaUiShellWindow(reduced);
        int reducedCompleted = 0;
        reducedWindow.StartupRevealCompleted += (_, _) => reducedCompleted++;
        try
        {
            reducedWindow.PrepareStartupScene(); reducedWindow.Show();
            AssertFalse(HasTrace(reducedWindow)); AssertEqual(1, reducedCompleted);
        }
        finally { reducedWindow.RequestClose(); }

        var quiet = XsrUiShellComposer.Compose(new XsrStateStoreBuilder().Build());
        quiet.Renderer.ReducedMotion = false;
        quiet.Renderer.OptionalMotionSuspended = true;
        var quietWindow = new AvaloniaUiShellWindow(quiet);
        int quietCompleted = 0;
        quietWindow.StartupRevealCompleted += (_, _) => quietCompleted++;
        try
        {
            quietWindow.PrepareStartupScene(); quietWindow.Show();
            // An inactive prepared window must still get its finite first-show decoration.
            // The ordinary scene remains quiet throughout; this is not a focus side effect.
            AssertTrue(HasTrace(quietWindow)); AssertEqual(0, quietCompleted);
            AssertEqual(.88, ((Grid)((Grid)quietWindow.Content!).Children[0]).Opacity);
            await WaitAsync(() => !HasTrace(quietWindow));
            AssertEqual(1, quietCompleted);
            AssertTrue(quiet.Renderer.OptionalMotionSuspended && quiet.Renderer.EffectiveReducedMotion);
            AssertEqual(1d, ((Grid)((Grid)quietWindow.Content!).Children[0]).Opacity);
        }
        finally { quiet.Renderer.ReducedMotion = true; quietWindow.RequestClose(); }

        var startupQuiet = XsrUiShellComposer.Compose(new XsrStateStoreBuilder().Build());
        startupQuiet.Renderer.ReducedMotion = false;
        var startupQuietWindow = new AvaloniaUiShellWindow(startupQuiet) { StartupMotionSuppressed = true };
        int startupQuietCompleted = 0;
        startupQuietWindow.StartupRevealCompleted += (_, _) => startupQuietCompleted++;
        try
        {
            await WaitAsync(() => AvaloniaUiMotion.CaptureDiagnostics() is { ActiveTracks: 0, TimerRunning: false });
            startupQuietWindow.PrepareStartupScene();
            // Normal scene preparation admits selection/hover/press presentation tracks.
            // Settle those first so the synchronous Show assertions isolate the window entrance.
            await WaitAsync(() => AvaloniaUiMotion.CaptureDiagnostics() is { ActiveTracks: 0, TimerRunning: false });
            startupQuietWindow.Show();
            AssertFalse(HasTrace(startupQuietWindow)); AssertEqual(1, startupQuietCompleted);
            AssertTrue(AvaloniaUiMotion.CaptureDiagnostics() is { ActiveTracks: 0, TimerRunning: false });
            AssertEqual(1d, ((Grid)((Grid)startupQuietWindow.Content!).Children[0]).Opacity);
        }
        finally { startupQuiet.Renderer.ReducedMotion = true; startupQuietWindow.RequestClose(); }

        var interrupted = XsrUiShellComposer.Compose(new XsrStateStoreBuilder().Build());
        interrupted.Renderer.ReducedMotion = false;
        var interruptedWindow = new AvaloniaUiShellWindow(interrupted);
        int interruptedCompleted = 0;
        interruptedWindow.StartupRevealCompleted += (_, _) => interruptedCompleted++;
        try
        {
            interruptedWindow.PrepareStartupScene(); interruptedWindow.Show();
            AssertTrue(HasTrace(interruptedWindow));
            interrupted.Renderer.ReducedMotion = true;
            await WaitAsync(() => !HasTrace(interruptedWindow));
            AssertEqual(1, interruptedCompleted);
            interrupted.Renderer.ReducedMotion = false;
            interruptedWindow.Surface.CommitScene();
            AssertFalse(HasTrace(interruptedWindow));
        }
        finally { interrupted.Renderer.ReducedMotion = true; interruptedWindow.RequestClose(); }

        var closing = XsrUiShellComposer.Compose(new XsrStateStoreBuilder().Build());
        closing.Renderer.ReducedMotion = false;
        var closingWindow = new AvaloniaUiShellWindow(closing);
        int closingCompleted = 0;
        closingWindow.StartupRevealCompleted += (_, _) => closingCompleted++;
        try
        {
            closingWindow.PrepareStartupScene(); closingWindow.Show();
            AssertTrue(HasTrace(closingWindow));
            closingWindow.CloseGuard = static () => false;
            closingWindow.RequestClose();
            AssertTrue(closingWindow.IsVisible && HasTrace(closingWindow));
            closingWindow.CloseGuard = null;
            closingWindow.RequestClose();
            AssertFalse(HasTrace(closingWindow));
            await WaitAsync(() => !closingWindow.IsVisible);
            AssertEqual(0, closingCompleted);
        }
        finally { closing.Renderer.ReducedMotion = true; closingWindow.RequestClose(); }

        // A canceled hidden preparation never publishes a startup completion or creates a trace.
        var discarded = new AvaloniaUiShellWindow(XsrUiShellComposer.Compose(new XsrStateStoreBuilder().Build()));
        int discardedCompleted = 0;
        discarded.StartupRevealCompleted += (_, _) => discardedCompleted++;
        discarded.PrepareStartupScene(); discarded.DiscardStartup();
        AssertFalse(discarded.IsVisible); AssertFalse(HasTrace(discarded)); AssertEqual(0, discardedCompleted);
        await WaitAsync(() => AvaloniaUiMotion.CaptureDiagnostics() is { ActiveTracks: 0, TimerRunning: false });
        owner.Activate();
        _windowEntranceVerified = true;

        static bool HasTrace(AvaloniaUiShellWindow target) => target.GetVisualDescendants().Any(control => control.Name == "NexaWindowEntrance");
        static async Task WaitAsync(Func<bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!ready()) await Task.Delay(10, timeout.Token).ConfigureAwait(true);
        }
    }
}
