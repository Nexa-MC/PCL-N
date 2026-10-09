using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexa.Core.Media;
using Nexa.UI.Next;
using Nexa.Xsr.State;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void MemoryPressurePreservesVisibleLeasesAndDisposesIdle()
    {
        AssertEqual(AvaloniaUiMemoryPressure.Critical, AvaloniaUiResourcePressure.Create(32 * 1048576L, 1024 * 1048576L, null, "fixture").Pressure);
        AssertEqual(AvaloniaUiMemoryPressure.Elevated, AvaloniaUiResourcePressure.Create(256 * 1048576L, 2048 * 1048576L, null, "fixture").Pressure);
        AssertEqual(AvaloniaUiMemoryPressure.Normal, AvaloniaUiResourcePressure.Create(2048 * 1048576L, 4096 * 1048576L, null, "fixture").Pressure);
        var image = PngImage.TryCreate(Convert.FromBase64String(TinyPngBase64))!;
        long charge = image.Width * image.Height * 8L;
        var pool = new AvaloniaUiRasterPool(charge, 4);
        using var visible = pool.Acquire(image, false, image.Width, out bool blocked, out _);
        AssertTrue(visible is not null && !blocked);
        pool.ApplyPressure(new(AvaloniaUiMemoryPressure.Critical, 1, 1024, null, "fixture"));
        var pressureFacts = pool.CaptureDiagnostics();
        AssertEqual(charge / 4, pressureFacts.AdmissionBudget);
        AssertEqual(AvaloniaUiMemoryPressure.Critical, pressureFacts.Pressure.Pressure);
        AssertEqual(charge, pressureFacts.Bytes);
        AssertEqual(1, pool.Retained.Leases);
        AssertEqual(0L, pool.Retained.DisposedBitmaps);
        visible!.Dispose();
        AssertEqual(0, pool.Retained.Entries);
        AssertEqual(1L, pool.Retained.DisposedBitmaps);
        AssertTrue(pool.Acquire(image, false, image.Width, out blocked, out _) is null && blocked);
        pool.ApplyPressure(new(AvaloniaUiMemoryPressure.Unknown, null, null, null, "unavailable"));
        using var restored = pool.Acquire(image, false, image.Width, out blocked, out _);
        AssertTrue(restored is not null && !blocked);
        var measured = AvaloniaUiResourcePressure.Read();
        if (OperatingSystem.IsLinux()) AssertTrue(measured.AvailableBytes.HasValue && measured.LimitBytes is > 0);
    }

    private static int RunNativeStartupSmoke()
    {
        return RunNativeStartupSmokeAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> RunNativeStartupSmokeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var startup = await AvaloniaUiStartupSession.StartWithAppearanceAsync([], disableHardwareAcceleration: true,
            new AvaloniaUiStartupAppearance(XsrUiThemeMode.Dark),
            localize: static text => text == "取消并退出" ? "Cancel and exit" : text, cancellationToken: timeout.Token);
        AssertEqual(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), startup.HardwareAccelerationDisabled);
        if (OperatingSystem.IsWindows())
            AssertEqual(Win32RenderingMode.Software, startup.RenderingConfiguration.Windows.RenderingMode.Single());
        if (OperatingSystem.IsLinux())
            AssertEqual(X11RenderingMode.Software, startup.RenderingConfiguration.Linux.RenderingMode.Single());
        AssertTrue(startup.FirstRenderElapsed.HasValue);
        Window startupWindow = await Dispatcher.UIThread.InvokeAsync(() =>
            ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).MainWindow!);
        await VerifyNativeStartupAppearanceAsync(startup, startupWindow, timeout.Token);
        AssertTrue(await Dispatcher.UIThread.InvokeAsync(() => startupWindow
            .GetVisualDescendants().OfType<Button>().Any(static button => (string?)button.Content == "Cancel and exit")));
        startup.ReportStage("settings-loading");
        await WaitForNativeStartupStageAsync(startup, startupWindow, "settings-loading", timeout.Token);
        startup.ReportFailure("initialization-failed");
        AssertTrue(!startup.Completion.IsCompleted);
        await WaitForNativeStartupStageAsync(startup, startupWindow, "initialization-failed", timeout.Token);
        await startup.InvokeAsync(() =>
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            AssertTrue(ReferenceEquals(startupWindow, desktop.MainWindow) && startupWindow.Topmost && startupWindow.IsVisible);
            AssertTrue(startupWindow.GetVisualDescendants().OfType<TextBlock>().Any(static text => text.Text == "initialization-failed"));
        }, timeout.Token);
        Task<bool> retry = startup.WaitForRetryAsync("initialization-failed-retry");
        await WaitForNativeStartupStageAsync(startup, startupWindow, "initialization-failed-retry", timeout.Token);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            AssertTrue(ReferenceEquals(startupWindow, desktop.MainWindow) && startupWindow.Topmost);
            Button button = desktop.MainWindow!.GetVisualDescendants().OfType<Button>()
                .Single(static button => (string?)button.Content == "重试");
            AssertTrue(button.IsVisible && button.IsEnabled && !retry.IsCompleted);
            button.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        });
        AssertTrue(await retry.WaitAsync(timeout.Token));
        await WaitForNativeStartupStageAsync(startup, startupWindow, "正在重试启动", timeout.Token);
        await startup.InvokeAsync(() =>
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            AssertTrue(ReferenceEquals(startupWindow, desktop.MainWindow) && startupWindow.Topmost && startupWindow.IsVisible);
            AssertTrue(startupWindow.GetVisualDescendants().OfType<TextBlock>().Any(static text => text.Text == "正在重试启动"));
            AssertEqual(0, AvaloniaUiMotion.CaptureDiagnostics().ActiveTracks);
        }, timeout.Token);
        var shell = XsrUiShellComposer.Compose(new XsrStateStoreBuilder().Build());
        shell.Renderer.ReducedMotion = true;
        var actions = new AvaloniaUiPlatformActions();
        await startup.PrepareShellAsync(shell, actions, timeout.Token);
        AvaloniaUiShellWindow? preparedWindow = null;
        await startup.InvokeAsync(() =>
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            preparedWindow = AssertNotNull(startup.PreparedWindow);
            AssertTrue(ReferenceEquals(startupWindow, desktop.MainWindow) && !preparedWindow.IsVisible);
            actions.RestoreWindow();
            AssertFalse(preparedWindow.IsVisible);
            AssertTrue(!desktop.Windows.OfType<AvaloniaUiShellWindow>().Any(window => window.IsVisible));
        }, timeout.Token);
        await startup.WarmUpShellAsync(shell, timeout.Token);
        global::Avalonia.Controls.Control[]? preparedControls = null;
        await startup.InvokeAsync(() =>
        {
            AssertTrue(ReferenceEquals(preparedWindow, startup.PreparedWindow));
            AssertTrue(preparedWindow!.Surface.Scene is { Count: > 0 } && !preparedWindow.IsVisible);
            preparedControls = preparedWindow.Surface.Children.ToArray();
            AssertTrue(startupWindow.Topmost && startupWindow.IsVisible);
        }, timeout.Token);
        Task<int> present = Task.Run(() => AvaloniaUiShellHost.Run(shell, platformActions: actions));
        while (!startup.ShellReadyElapsed.HasValue) await Task.Delay(10, timeout.Token);
        AssertEqual(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), startup.HardwareAccelerationDisabled);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            AssertTrue(ReferenceEquals(preparedWindow, desktop.MainWindow) && desktop.Windows.Count == 1);
            AssertTrue(preparedControls!.All(control => preparedWindow!.Surface.Children.Contains(control)));
            AssertFalse(startupWindow.IsVisible);
            AssertTrue(((AvaloniaUiShellWindow)desktop.MainWindow!).Surface.Scene is { Count: > 0 });
            desktop.MainWindow!.Close();
        });
        AssertEqual(0, await present.WaitAsync(timeout.Token));
        Console.WriteLine("PASS: native startup follows actual themes, retires splash motion, stays topmost through loading/retry and presents the same warmed window.");
        return 0;
    }
}
