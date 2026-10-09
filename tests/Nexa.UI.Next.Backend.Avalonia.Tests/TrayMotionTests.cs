using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr.State;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static bool _trayVisibilityVerified;
    private static void TrayVisibilityRetargetsRetainedContentAndRetiresLifetimes() => AssertTrue(_trayVisibilityVerified);

    private static async Task VerifyTrayVisibilityAsync(AvaloniaUiShellWindow owner)
    {
        _trayVisibilityVerified = false;
        var shell = XsrUiShellComposer.Compose(new XsrStateStoreBuilder().Build());
        shell.Renderer.ReducedMotion = true;
        var window = new AvaloniaUiShellWindow(shell);
        var actions = new AvaloniaUiPlatformActions();
        actions.Attach(window);
        int reveals = 0;
        window.StartupRevealCompleted += (_, _) => reveals++;
        try
        {
            window.PrepareStartupScene(); window.Show(); window.Activate(); window.UpdateLayout();
            AssertEqual(1, reveals);
            shell.Renderer.ReducedMotion = false;
            Grid content = TrayPresentation(window);
            Size nativeSize = window.Bounds.Size;
            Size viewport = window.Surface.Bounds.Size;
            Point origin = window.Surface.TranslatePoint(default, window)!.Value;
            var controls = window.Surface.Children.ToArray();
            nint? handle = window.TryGetPlatformHandle()?.Handle;
            void VerifyRetainedPresentation()
            {
                AssertEqual(nativeSize, window.Bounds.Size);
                AssertEqual(viewport, window.Surface.Bounds.Size);
                AssertEqual(origin, window.Surface.TranslatePoint(default, window)!.Value);
                AssertEqual(handle, window.TryGetPlatformHandle()?.Handle);
                AssertEqual(1d, window.Opacity);
                AssertTrue(content.Clip is null && content.RenderTransform is null);
                AssertTrue(controls.All(control => window.Surface.Children.Contains(control)));
                AssertEqual(1, reveals);
            }

            // A duplicate hide keeps the same pending target; a reversal continues from the
            // actual presented opacity and cancels the old deferred native Hide callback.
            actions.HideWindow();
            AssertTrue(window.IsVisible && window.IsHidingToTray);
            AssertEqual(1d, content.Opacity);
            await WaitForTrayMotionAsync(() => content.Opacity is > 0 and < 1);
            double presented = content.Opacity;
            actions.HideWindow(); actions.RestoreWindow();
            AssertEqual(presented, content.Opacity);
            AssertFalse(window.IsHidingToTray);
            await WaitForTrayMotionAsync(() => content.Opacity == 1);
            await Task.Delay(AvaloniaMotionTokens.TrayHideMilliseconds + 40).ConfigureAwait(true);
            AssertTrue(window.IsVisible);
            VerifyRetainedPresentation();

            actions.HideWindow();
            await WaitForTrayMotionAsync(() => !window.IsVisible);
            AssertEqual(0d, content.Opacity);
            shell.Renderer.OptionalMotionSuspended = true;
            AssertTrue(shell.Renderer.OptionalMotionSuspended && shell.Renderer.EffectiveReducedMotion);
            // Scene suspension is not the explicit preference: restoring must still animate.
            actions.RestoreWindow();
            AssertTrue(window.IsVisible); AssertEqual(0d, content.Opacity);
            await WaitForTrayMotionAsync(() => content.Opacity is > 0 and < 1);
            presented = content.Opacity;
            actions.HideWindow();
            AssertEqual(presented, content.Opacity);
            await WaitForTrayMotionAsync(() => !window.IsVisible);
            actions.RestoreWindow();
            await WaitForTrayMotionAsync(() => content.Opacity == 1);
            VerifyRetainedPresentation();
            AssertTrue(shell.Renderer.OptionalMotionSuspended);
            shell.Renderer.OptionalMotionSuspended = false;

            // Disabling the tray during a still-visible fade must reverse the pending hide;
            // otherwise completion would strand a window with no tray icon to restore it.
            using (var integration = new AvaloniaUiDesktopIntegration(window, actions.RestoreWindow, () => { }, value => value))
            {
                integration.Apply(new(true));
                actions.HideWindow();
                await WaitForTrayMotionAsync(() => content.Opacity is > 0 and < 1);
                integration.Apply(new(false));
                AssertFalse(window.IsHidingToTray);
                await WaitForTrayMotionAsync(() => content.Opacity == 1);
                await Task.Delay(AvaloniaMotionTokens.TrayHideMilliseconds + 40).ConfigureAwait(true);
                AssertTrue(window.IsVisible);
            }

            // Explicit reduced motion settles synchronously on a new command, and changing
            // it during an existing transition retires that track on the shared next frame.
            actions.HideWindow();
            await WaitForTrayMotionAsync(() => content.Opacity is > 0 and < 1);
            shell.Renderer.ReducedMotion = true;
            await WaitForTrayMotionAsync(() => !window.IsVisible);
            actions.RestoreWindow();
            AssertTrue(window.IsVisible); AssertEqual(1d, content.Opacity);
            actions.HideWindow(); AssertFalse(window.IsVisible); AssertEqual(0d, content.Opacity);
            shell.Renderer.ReducedMotion = false;
            actions.RestoreWindow();
            await WaitForTrayMotionAsync(() => content.Opacity is > 0 and < 1);
            shell.Renderer.ReducedMotion = true;
            await WaitForTrayMotionAsync(() => content.Opacity == 1);
            AssertTrue(window.IsVisible);
            VerifyRetainedPresentation();

            window.WindowState = WindowState.Maximized;
            actions.HideWindow(); actions.RestoreWindow();
            AssertEqual(WindowState.Maximized, window.WindowState);
            // Custom minimize-to-tray enters the fade before the native surface disappears.
            shell.Renderer.ReducedMotion = false;
            window.MinimizeToTrayRequested = () => { window.HideToTray(); return true; };
            actions.MinimizeWindow();
            AssertTrue(window.IsVisible && window.IsHidingToTray);
            AssertEqual(WindowState.Maximized, window.WindowState);
            await WaitForTrayMotionAsync(() => !window.IsVisible);
            actions.RestoreWindow();
            AssertEqual(WindowState.Maximized, window.WindowState);
            await WaitForTrayMotionAsync(() => content.Opacity == 1);
            window.MinimizeToTrayRequested = null;
            shell.Renderer.ReducedMotion = true;
            actions.MinimizeWindow();
            AssertEqual(WindowState.Minimized, window.WindowState);
            actions.HideWindow(); actions.RestoreWindow();
            AssertEqual(WindowState.Maximized, window.WindowState);
            // Native/system minimizing also remembers the prior state without the platform
            // command path; closing to the tray must not reopen a maximized shell as normal.
            window.WindowState = WindowState.Minimized;
            window.HideToTray(); actions.RestoreWindow();
            AssertEqual(WindowState.Maximized, window.WindowState);
            window.WindowState = WindowState.Normal;

            shell.Renderer.ReducedMotion = false;
            actions.HideWindow();
            await WaitForTrayMotionAsync(() => content.Opacity is > 0 and < 1);
            window.RequestClose();
            await WaitForTrayMotionAsync(() => !window.IsVisible);
            actions.RestoreWindow(); AssertFalse(window.IsVisible);
        }
        finally { shell.Renderer.ReducedMotion = true; window.RequestClose(); }

        // Startup interruption completes the once-only reveal and removes its competing
        // track. Canceling/discarding the retained attempt retires a pending tray callback.
        var startupShell = XsrUiShellComposer.Compose(new XsrStateStoreBuilder().Build());
        var startup = new AvaloniaUiShellWindow(startupShell);
        int startupReveals = 0;
        startup.StartupRevealCompleted += (_, _) => startupReveals++;
        startup.PrepareStartupScene(); startup.Show();
        AssertTrue(startup.GetVisualDescendants().Any(control => control.Name == "NexaWindowEntrance"));
        double startupOpacity = TrayPresentation(startup).Opacity;
        startup.HideToTray();
        AssertEqual(startupOpacity, TrayPresentation(startup).Opacity);
        AssertEqual(1, startupReveals);
        AssertFalse(startup.GetVisualDescendants().Any(control => control.Name == "NexaWindowEntrance"));
        startup.RestoreFromTray();
        await WaitForTrayMotionAsync(() => TrayPresentation(startup).Opacity == 1);
        AssertEqual(1, startupReveals);
        startup.HideToTray();
        await WaitForTrayMotionAsync(() => TrayPresentation(startup).Opacity is > 0 and < 1);
        startup.DiscardStartup(); startup.RestoreFromTray();
        AssertFalse(startup.IsVisible); AssertEqual(1, startupReveals);
        await WaitForTrayMotionAsync(() => AvaloniaUiMotion.CaptureDiagnostics() is { ActiveTracks: 0, TimerRunning: false });
        owner.Activate();
        _trayVisibilityVerified = true;

    }

    private static Grid TrayPresentation(AvaloniaUiShellWindow window) => (Grid)((Grid)window.Content!).Children[0];

    private static async Task WaitForTrayMotionAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!ready()) await Task.Delay(10, timeout.Token).ConfigureAwait(true);
    }
}
