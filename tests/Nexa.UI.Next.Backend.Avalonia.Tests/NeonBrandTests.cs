using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Nexa.Xsr.State;
using SkiaSharp;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void NeonThemeSelectionHonorsExplicitAndSystemModes()
    {
        AssertFalse(AvaloniaUiBrandImages.ResolveDark(XsrUiThemeMode.Light, ThemeVariant.Dark));
        AssertTrue(AvaloniaUiBrandImages.ResolveDark(XsrUiThemeMode.Dark, ThemeVariant.Light));
        AssertFalse(AvaloniaUiBrandImages.ResolveDark(XsrUiThemeMode.System, ThemeVariant.Light));
        AssertTrue(AvaloniaUiBrandImages.ResolveDark(XsrUiThemeMode.System, ThemeVariant.Dark));
        bool rejected = false;
        try { AvaloniaUiBrandImages.ResolveDark((XsrUiThemeMode)99, ThemeVariant.Dark); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        AssertTrue(rejected);
    }

    private static async Task VerifyNativeBrandAssetsAndCloseDecoration()
    {
        using Stream light = AssertNotNull(typeof(Program).Assembly.GetManifestResourceStream("Nexa.Brand.Tests.light.png"));
        using Stream dark = AssertNotNull(typeof(Program).Assembly.GetManifestResourceStream("Nexa.Brand.Tests.dark.png"));
        using var images = AvaloniaUiBrandImages.Load(light, dark);
        AssertEqual(new global::Avalonia.PixelSize(512, 512), images.Resolve(false).PixelSize);
        AssertEqual(new global::Avalonia.PixelSize(512, 512), images.Resolve(true).PixelSize);
        using (var lightPixels = ReadBrandPixels(images.Resolve(false)))
        using (var darkPixels = ReadBrandPixels(images.Resolve(true)))
        {
            foreach (var pixels in new[] { lightPixels, darkPixels })
                foreach (var (x, y) in new[] { (0, 0), (511, 0), (0, 511), (511, 511) })
                    AssertEqual((byte)0, pixels.GetPixel(x, y).Alpha);
            AssertTrue(lightPixels.GetPixel(70, 90) != darkPixels.GetPixel(70, 90));
        }

        var shell = new XsrUiShell(new XsrStateStoreBuilder().Build());
        shell.Renderer.ReducedMotion = true;
        var window = new AvaloniaUiShellWindow(shell);
        window.SetOwnedBrandImages(images); // Native window owns both bitmaps until close.
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        var tray = new TrayIcon();
        int trayCreations = 0;
        using var integration = new AvaloniaUiDesktopIntegration(window, () => { }, () => { }, value => value,
            () => { trayCreations++; return tray; });
        try
        {
            window.Show();
            await Task.Delay(30).ConfigureAwait(true);
            window.Surface.CommitScene();
            integration.Apply(new(true));
            WindowIcon lightIcon = images.ResolveIcon(false), darkIcon = images.ResolveIcon(true);
            AssertTrue(ReferenceEquals(lightIcon, window.Icon));
            AssertTrue(ReferenceEquals(window.Icon, tray.Icon));
            AssertEqual(1, trayCreations);

            // A committed Light scene remains authoritative when the native window uses
            // Dark; OS notifications reach the renderer through the separate appearance edge.
            window.RequestedThemeVariant = ThemeVariant.Dark;
            await Task.Delay(20).ConfigureAwait(true);
            AssertEqual(ThemeVariant.Dark, window.ActualThemeVariant);
            AssertTrue(ReferenceEquals(lightIcon, window.Icon));
            shell.Renderer.ColorScheme = new(true);
            window.Surface.CommitScene();
            AssertTrue(ReferenceEquals(darkIcon, window.Icon));
            AssertTrue(ReferenceEquals(window.Icon, tray.Icon));
            shell.Renderer.ColorScheme = default;
            window.Surface.CommitScene();
            AssertTrue(ReferenceEquals(lightIcon, window.Icon));
            AssertTrue(ReferenceEquals(window.Icon, tray.Icon));
            AssertEqual(1, trayCreations); // Theme changes update the existing tray.

            shell.Renderer.ReducedMotion = false;
            window.RequestClose();
            Image closing = window.GetVisualDescendants().OfType<Image>()
                .Single(image => ReferenceEquals(image.Source, images.Resolve(false)));
            shell.Renderer.ColorScheme = new(true);
            window.Surface.CommitScene();
            AssertTrue(ReferenceEquals(images.Resolve(true), closing.Source));
            AssertTrue(ReferenceEquals(darkIcon, window.Icon));
            AssertTrue(ReferenceEquals(window.Icon, tray.Icon));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            AssertTrue(images.IsDisposed);
            bool disposedRejected = false;
            try { images.Resolve(false); }
            catch (ObjectDisposedException) { disposedRejected = true; }
            AssertTrue(disposedRejected);
        }
        finally
        {
            if (!closed.Task.IsCompleted)
            {
                shell.Renderer.ReducedMotion = true;
                window.Close();
            }
        }
        Console.WriteLine("PASS: actual neon assets preserve transparency, committed theme updates one window/tray, current close image and ownership");
    }

    private static SKBitmap ReadBrandPixels(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default); stream.Position = 0;
        return AssertNotNull(SKBitmap.Decode(stream));
    }
}
