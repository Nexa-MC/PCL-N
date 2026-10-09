using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Chrome;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr.State;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static int RunNativeCornerSmoke()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native corner smoke requires Windows.");
        return AppBuilder.Configure<NativeCornerProbeApp>().UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                CompositionMode = [Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition,
                    Win32CompositionMode.RedirectionSurface],
            }).StartWithClassicDesktopLifetime([]);
    }

    public sealed class NativeCornerProbeApp : Application
    {
        public override void OnFrameworkInitializationCompleted()
        {
            // Match production: an absent Fluent theme can hide a stale implicit caption.
            Styles.Add(new global::Avalonia.Themes.Fluent.FluentTheme());
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var shell = XsrUiShellComposer.Compose(new XsrStateStoreBuilder().Build());
                shell.Renderer.ReducedMotion = true;
                var window = new AvaloniaUiShellWindow(shell);
                var actions = new AvaloniaUiPlatformActions();
                actions.Attach(window);
                int openings = 0, reveals = 0;
                window.StartupRevealCompleted += (_, _) => reveals++;
                VerifyNativeCornerManagedDecorations(window);
                window.PrepareStartupScene();
                AssertFalse(window.IsVisible);
                AssertTrue(window.Surface.Scene is { Count: > 0 });
                VerifyNativeCornerManagedDecorations(window);
                nint handle = window.TryGetPlatformHandle()!.Handle;
                AssertTrue(handle != 0);
                desktop.MainWindow = window;
                window.Opened += (_, _) =>
                {
                    // These are synchronous public Opened observations: a delayed repair
                    // must not make an incorrect first show or tray restoration pass.
                    AssertEqual(handle, window.TryGetPlatformHandle()!.Handle);
                    VerifyNativeCornerManagedDecorations(window);
                    VerifyNativeDecorationPolicy(window);
                    openings++;
                    if (openings == 1)
                        Dispatcher.UIThread.Post(() => _ = VerifyNativeCornerLifecycleAsync(
                            desktop, window, actions, handle, () => openings, () => reveals));
                };
            }
            base.OnFrameworkInitializationCompleted();
        }
    }

    private static async Task VerifyNativeCornerLifecycleAsync(
        IClassicDesktopStyleApplicationLifetime desktop, AvaloniaUiShellWindow window,
        AvaloniaUiPlatformActions actions, nint handle, Func<int> openings, Func<int> reveals)
    {
        try
        {
            await WaitForNativeCornerLayoutAsync(() => NativeCornerViewportMatches(window, 850, 500));
            VerifyCompositedViewport(window, 850, 500);
            AssertEqual(1, openings()); AssertEqual(1, reveals());
            window.Width += 100; window.Height += 50;
            await WaitForNativeCornerLayoutAsync(() => NativeCornerViewportMatches(window, 950, 550));
            VerifyCompositedViewport(window, 950, 550);

            // A normal hidden window can reopen without any size or state notification.
            // Force the stale DWM policy so this cannot pass merely because it stayed off.
            for (int restore = 0; restore < 2; restore++)
            {
                window.Hide();
                ForceNativeCornerStaleFrame(window);
                window.Show(); window.Activate();
                VerifyNativeCornerRestoration(window, handle, WindowState.Normal, reveals);
                VerifyCompositedViewport(window, 950, 550);
            }
            AssertEqual(3, openings());
            actions.HideWindow();
            ForceNativeCornerStaleFrame(window);
            actions.RestoreWindow();
            VerifyNativeCornerRestoration(window, handle, WindowState.Normal, reveals);
            VerifyCompositedViewport(window, 950, 550);
            AssertEqual(4, openings());

            window.WindowState = WindowState.Maximized;
            await WaitForNativeCornerLayoutAsync(() => NativeCornerMaximizedViewportMatches(window));
            VerifyNativeCornerMaximizedViewport(window);
            Size maximizedViewport = window.Surface.Bounds.Size;
            actions.HideWindow();
            ForceNativeCornerStaleFrame(window);
            actions.RestoreWindow();
            VerifyNativeCornerRestoration(window, handle, WindowState.Maximized, reveals);
            await WaitForNativeCornerLayoutAsync(() => NativeCornerMaximizedViewportMatches(window));
            VerifyNativeCornerMaximizedViewport(window);
            AssertEqual(maximizedViewport, window.Surface.Bounds.Size);
            AssertEqual(5, openings());

            window.WindowState = WindowState.Normal;
            await WaitForNativeCornerLayoutAsync(() => NativeCornerViewportMatches(window, 950, 550));
            VerifyCompositedViewport(window, 950, 550);
            window.WindowState = WindowState.Minimized;
            await WaitForNativeCornerLayoutAsync(() => window.WindowState == WindowState.Minimized);
            window.WindowState = WindowState.Normal;
            await WaitForNativeCornerLayoutAsync(() => NativeCornerViewportMatches(window, 950, 550));
            VerifyCompositedViewport(window, 950, 550);
            window.TransparencyLevelHint = [WindowTransparencyLevel.None];
            await WaitForNativeCornerLayoutAsync(() => !window.UsesCompositedEdges
                && ((Control)window.Content!).Bounds.Size == window.Surface.Bounds.Size);
            AssertFalse(window.UsesCompositedEdges);
            AssertEqual(((Control)window.Content!).Bounds.Size, window.Surface.Bounds.Size);
            AssertTrue(Math.Abs(window.Surface.Bounds.Width - 950) <= 1 / window.RenderScaling);
            AssertTrue(window.Background is LinearGradientBrush fallback && fallback.GradientStops.All(stop => stop.Color.A == 255));
            window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];
            await WaitForNativeCornerLayoutAsync(() => window.UsesCompositedEdges
                && NativeCornerViewportMatches(window, 950, 550));
            VerifyCompositedViewport(window, 950, 550);
            AssertEqual(handle, window.TryGetPlatformHandle()!.Handle);
            AssertEqual(1, reveals());
            Console.WriteLine("PASS: empty managed decorations before show; immediate DWM suppression on every opening; same HWND, viewport and input origin through repeated normal/maximized tray restores; once-only entrance, native composition and opaque fallback; optional DWM attributes checked where readable");
            desktop.Shutdown(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); desktop.Shutdown(1); }
    }

    private static void VerifyNativeCornerManagedDecorations(AvaloniaUiShellWindow window)
    {
        ControlTheme theme = AssertNotNull(window.WindowDecorationsTheme);
        AssertEqual(typeof(WindowDrawnDecorations), theme.TargetType);
        var template = (IWindowDrawnDecorationsTemplate)theme.Setters.OfType<Setter>()
            .Single(setter => setter.Property == WindowDrawnDecorations.TemplateProperty).Value!;
        VerifyNativeCornerEmptyDecorationContent(template.Build().Result);
        // Decorations belong to the host above Window, alongside its visual layers.
        var host = (StyledElement)AssertNotNull(window.GetVisualParent());
        var decorations = host.GetLogicalDescendants().OfType<WindowDrawnDecorations>().Single();
        AssertTrue(ReferenceEquals(theme, decorations.Theme));
        VerifyNativeCornerEmptyDecorationContent(AssertNotNull(decorations.Content));
    }

    private static void VerifyNativeCornerEmptyDecorationContent(WindowDrawnDecorationsContent content)
    {
        AssertTrue(content.Overlay is null);
        AssertTrue(content.Underlay is null);
        AssertTrue(content.FullscreenPopover is null);
    }

    private static void ForceNativeCornerStaleFrame(AvaloniaUiShellWindow window)
    {
        AssertFalse(window.IsVisible);
        AvaloniaWindowsFrame.SetNonClientRendering(window, true);
        AssertEqual(0, DwmGetWindowAttribute(window.TryGetPlatformHandle()!.Handle, 1, out int enabled, sizeof(int)));
        AssertEqual(1, enabled);
    }

    private static void VerifyNativeCornerRestoration(AvaloniaUiShellWindow window, nint handle,
        WindowState state, Func<int> reveals)
    {
        AssertTrue(window.IsVisible);
        AssertEqual(state, window.WindowState);
        AssertEqual(handle, window.TryGetPlatformHandle()!.Handle);
        AssertEqual(1, reveals());
        VerifyNativeCornerManagedDecorations(window);
        VerifyNativeDecorationPolicy(window);
    }

    private static bool NativeCornerViewportMatches(AvaloniaUiShellWindow window, double width, double height)
        => Math.Abs(width - window.Surface.Bounds.Width) <= 1 / window.RenderScaling
            && Math.Abs(height - window.Surface.Bounds.Height) <= 1 / window.RenderScaling;

    private static bool NativeCornerMaximizedViewportMatches(AvaloniaUiShellWindow window)
        => window.WindowState == WindowState.Maximized
            && ((Control)window.Content!).Bounds.Size == window.Surface.Bounds.Size;

    private static async Task WaitForNativeCornerLayoutAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // State setters can preserve the old bounds while a render-priority frame repair
        // is still queued. This is a layout barrier; Opened policy checks never use it.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private static void VerifyNativeCornerMaximizedViewport(AvaloniaUiShellWindow window)
    {
        VerifyNoClientCornerClipping(window);
        VerifyNativeDecorationPolicy(window);
        VerifyNativeCornerManagedDecorations(window);
        var root = (Control)window.Content!;
        AssertEqual(root.Bounds.Size, window.Surface.Bounds.Size);
        AssertEqual(new Point(0, 0), window.Surface.TranslatePoint(default, root)!.Value);
    }

    private static void VerifyCompositedViewport(AvaloniaUiShellWindow window, double width, double height)
    {
        Console.WriteLine($"transparency={window.ActualTransparencyLevel}, layered={AvaloniaWindowsFrame.IsLayered(window)}, scene={window.Surface.Bounds.Size}");
        AssertTrue(window.UsesCompositedEdges);
        AssertFalse(AvaloniaWindowsFrame.IsLayered(window));
        VerifyNoClientCornerClipping(window);
        VerifyNativeDecorationPolicy(window);
        VerifyNativeCornerManagedDecorations(window);
        // Native pixel bounds round fractional DIPs at scales such as 125%.
        AssertTrue(Math.Abs(width - window.Surface.Bounds.Width) <= 1 / window.RenderScaling);
        AssertTrue(Math.Abs(height - window.Surface.Bounds.Height) <= 1 / window.RenderScaling);
        var root = (Control)window.Content!;
        AssertEqual(new Size(window.Surface.Bounds.Width + 48, window.Surface.Bounds.Height + 48), root.Bounds.Size);
        AssertEqual(new Point(24, 24), window.Surface.TranslatePoint(default, root)!.Value);
        AssertEqual(new Point(0, 0), root.TranslatePoint(new Point(24, 24), window.Surface)!.Value);
        AssertTrue(window.Background is ISolidColorBrush { Color.A: 0 });
        var chrome = (Border)window.Surface.Parent!.Parent!;
        AssertEqual(new CornerRadius(24), chrome.CornerRadius);
        AssertTrue(chrome.Clip is RectangleGeometry { RadiusX: 24, RadiusY: 24 });
        AssertTrue(chrome.Background is LinearGradientBrush background && background.GradientStops.All(stop => stop.Color.A == 255));
    }
    private static void VerifyNoClientCornerClipping(AvaloniaUiShellWindow window)
    {
        nint region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            int kind = GetWindowRgn(window.TryGetPlatformHandle()!.Handle, region);
            Console.WriteLine($"region={kind}");
            if (kind != 0)
            {
                AssertTrue(GetWindowRectForChrome(window.TryGetPlatformHandle()!.Handle, out var bounds));
                var root = (Control)window.Content!;
                var origin = root.PointToScreen(default);
                int left = origin.X - bounds.Left, top = origin.Y - bounds.Top;
                int right = left + (int)Math.Round(root.Bounds.Width * window.RenderScaling) - 1;
                int bottom = top + (int)Math.Round(root.Bounds.Height * window.RenderScaling) - 1;
                Console.WriteLine($"region corners={PtInRegion(region, left, top)},{PtInRegion(region, right, top)},{PtInRegion(region, left, bottom)},{PtInRegion(region, right, bottom)}");
                AssertTrue(PtInRegion(region, left, top) != 0 && PtInRegion(region, right, top) != 0
                    && PtInRegion(region, left, bottom) != 0 && PtInRegion(region, right, bottom) != 0);
            }
        }
        finally { _ = DeleteObject(region); }
    }
    private static void VerifyNativeDecorationPolicy(AvaloniaUiShellWindow window)
    {
        nint handle = window.TryGetPlatformHandle()!.Handle;
        AssertEqual(0, DwmGetWindowAttribute(handle, 1, out int nonClientEnabled, sizeof(int)));
        AssertEqual(0, nonClientEnabled);
        AssertEqual(0, DwmIsCompositionEnabled(out int compositionEnabled));
        AssertEqual(1, compositionEnabled);
        VerifyNativeCornerAttributeWhenReadable(handle, 3, 0, "transitions forced disabled");
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            VerifyNativeCornerAttributeWhenReadable(handle, 33, 1, "corner preference"); // DWMWCP_DONOTROUND
            VerifyNativeCornerAttributeWhenReadable(handle, AvaloniaWindowsFrame.BorderColorAttribute,
                unchecked((int)AvaloniaWindowsFrame.NoBorderColor), "border color");
        }
        long style = GetWindowLongPtrForChrome(handle, -16).ToInt64();
        const long nativeCapabilities = 0x00C00000 | 0x00040000 | 0x00020000 | 0x00010000;
        AssertEqual(nativeCapabilities, style & nativeCapabilities); // caption, resize, minimize, maximize
    }

    private static void VerifyNativeCornerAttributeWhenReadable(nint handle, int attribute, int expected, string name)
    {
        int result = DwmGetWindowAttribute(handle, attribute, out int value, sizeof(int));
        // These attributes are documented for DwmSetWindowAttribute; getter support varies.
        // An unreadable setter attribute is separate from a failed mandatory native check.
        if (result is unchecked((int)0x80070057) or unchecked((int)0x80004001))
        {
            Console.WriteLine($"UNVERIFIED: DWM {name} query unsupported (HRESULT 0x{result:X8}).");
            return;
        }
        AssertEqual(0, result);
        AssertEqual(expected, value);
    }
    [LibraryImport("dwmapi.dll")] private static partial int DwmIsCompositionEnabled(out int enabled);
    [LibraryImport("dwmapi.dll")] private static partial int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct ChromeNativeRect { public int Left, Top, Right, Bottom; }
    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRectForChrome(nint window, out ChromeNativeRect rect);
    [LibraryImport("gdi32.dll")] private static partial int PtInRegion(nint region, int x, int y);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static partial nint GetWindowLongPtrForChrome(nint window, int index);
    [LibraryImport("user32.dll")] private static partial int GetWindowRgn(nint window, nint region);
    [LibraryImport("gdi32.dll")] private static partial nint CreateRectRgn(int left, int top, int right, int bottom);
    [LibraryImport("gdi32.dll")] private static partial int DeleteObject(nint region);
}

