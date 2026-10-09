using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Nexa.UI.Next;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>
/// The desktop lifetime contract for the product shell, extracted from the Application class so
/// tests can run it under a headless platform. The splash is pure decoration: it never becomes
/// the application main window and never owns the process lifetime. The shell window is the
/// main window, and the automatic <see cref="ShutdownMode.OnMainWindowClose"/> contract
/// terminates the process when it closes for real — including after the close-collapse
/// animation finishes.
/// </summary>
public static class AvaloniaUiShellLifetime
{
    /// <summary>Creates the real shell while the startup window still owns the lifetime.</summary>
    internal static AvaloniaUiShellWindow Prepare(XsrUiShell shell, Stream? windowIcon)
    {
        var window = new AvaloniaUiShellWindow(shell, windowIcon);
        var pressure = new AvaloniaUiResourcePressureSession();
        window.Closed += (_, _) => pressure.Dispose();
        return window;
    }

    internal static void PresentPrepared(IClassicDesktopStyleApplicationLifetime desktop, AvaloniaUiShellWindow window)
    {
        desktop.MainWindow = window;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    public static AvaloniaUiShellWindow Compose(
        IClassicDesktopStyleApplicationLifetime desktop,
        XsrUiShell shell,
        Stream? splashIcon,
        Stream? windowIcon)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(shell);

        // Restoring the automatic lifetime contract up front is the whole point: with the
        // splash handling startup there is no window that should keep the process alive by
        // itself, and closing the shell window must always exit the process.
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

        AvaloniaSplashWindow? splash = splashIcon is null ? null : new AvaloniaSplashWindow(splashIcon);
        if (splash is not null)
        {
            splash.SetAppearance(new(shell.Renderer.ColorScheme.IsDark ? XsrUiThemeMode.Dark : XsrUiThemeMode.Light,
                shell.Renderer.EffectiveReducedMotion, ProductVersion: shell.Version));
            splash.Show();
        }
        AvaloniaUiShellWindow window = Prepare(shell, windowIcon);
        if (splash is not null)
        {
            // Hard guarantee that a lost reveal event can never leave the topmost splash stuck
            // over the launcher: whichever side reaches the icon first closes it, and the
            // guarded close makes every other caller a no-op.
            DispatcherTimer fallback = new() { Interval = TimeSpan.FromSeconds(2) };
            bool splashClosed = false;
            void CloseSplash()
            {
                if (splashClosed)
                {
                    return;
                }

                splashClosed = true;
                fallback.Stop();
                splash.Close();
            }

            window.StartupRevealCompleted += (_, _) => CloseSplash();
            window.Closed += (_, _) => CloseSplash();
            fallback.Tick += (_, _) => CloseSplash();
            fallback.Start();
        }

        window.PrepareStartupScene();
        PresentPrepared(desktop, window);
        return window;
    }
}
