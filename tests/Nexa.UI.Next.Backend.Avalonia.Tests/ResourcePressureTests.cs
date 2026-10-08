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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var startup = await AvaloniaUiStartupSession.StartAsync([], disableHardwareAcceleration: true,
            localize: static text => text == "取消并退出" ? "Cancel and exit" : text, cancellationToken: timeout.Token);
        AssertEqual(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), startup.HardwareAccelerationDisabled);
        if (OperatingSystem.IsWindows())
            AssertEqual(Win32RenderingMode.Software, startup.RenderingConfiguration.Windows.RenderingMode.Single());
        if (OperatingSystem.IsLinux())
            AssertEqual(X11RenderingMode.Software, startup.RenderingConfiguration.Linux.RenderingMode.Single());
        AssertTrue(startup.FirstRenderElapsed.HasValue);
        AssertTrue(await Dispatcher.UIThread.InvokeAsync(() =>
            ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).MainWindow!
                .GetVisualDescendants().OfType<Button>().Any(static button => (string?)button.Content == "Cancel and exit")));
        startup.ReportStage("settings-loading");
        bool stageVisible = await Dispatcher.UIThread.InvokeAsync(() =>
            ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).MainWindow!
                .GetVisualDescendants().OfType<TextBlock>().Any(static text => text.Text == "settings-loading"));
        AssertTrue(stageVisible);
        startup.ReportFailure("initialization-failed");
        AssertTrue(!startup.Completion.IsCompleted);
        AssertTrue(await Dispatcher.UIThread.InvokeAsync(() =>
            ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).MainWindow!
                .GetVisualDescendants().OfType<TextBlock>().Any(static text => text.Text == "initialization-failed")));
        var shell = XsrUiShellComposer.Compose(new XsrStateStoreBuilder().Build());
        shell.Renderer.ReducedMotion = true;
        Task<int> present = Task.Run(() => AvaloniaUiShellHost.Run(shell));
        while (!startup.ShellReadyElapsed.HasValue) await Task.Delay(10, timeout.Token);
        AssertEqual(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), startup.HardwareAccelerationDisabled);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            AssertTrue(desktop.MainWindow is AvaloniaUiShellWindow && desktop.Windows.Count == 1);
            desktop.MainWindow!.Close();
        });
        AssertEqual(0, await present.WaitAsync(timeout.Token));
        Console.WriteLine("PASS: early native startup renders, stays responsive, reports failure and hands off one lifetime.");
        return 0;
    }
}
