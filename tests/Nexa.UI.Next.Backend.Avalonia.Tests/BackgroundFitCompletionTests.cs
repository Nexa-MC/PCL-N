using Nexa.Core.Media;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using SkiaSharp;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void VerifyBackgroundFitAndImageOpacityDrawing()
    {
        using var source = new SKBitmap(20, 10);
        source.Erase(SKColors.Blue);
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 5; x++)
            { source.SetPixel(x, y, SKColors.Red); source.SetPixel(19 - x, y, SKColors.Lime); }
        using var encodedImage = SKImage.FromBitmap(source);
        using var bytes = encodedImage.Encode(SKEncodedImageFormat.Png, 100);
        PngImage image = AssertNotNull(PngImage.TryCreate(bytes.ToArray()));
        AvaloniaUiRasterPool pool = new(1_048_576, 4);
        var control = new AvaloniaUiSceneNodeControl(_ => { }, _ => { }, () => true, rasterPool: pool);
        XsrUiTree tree = new();
        var node = Node(tree.Create("background-fit"), XsrUiSemanticRole.Image) with
        {
            Rect = new(0, 0, 100, 100),
            ImageSource = null,
            VisualStyle = new XsrUiVisualStyle { Background = new(255, 255, 255) }.Snapshot(),
        };
        var recipe = new XsrUiRasterImage(image, []) { FitToBounds = true };
        try
        {
            control.Apply(node with { RasterImage = recipe }); // Existing full-image callers retain containment.
            using (var pixels = DrawComposite(control))
            {
                AssertEqual(SKColors.White, pixels.GetPixel(50, 5));
                AssertEqual(SKColors.Red, pixels.GetPixel(5, 50));
                AssertEqual(SKColors.Blue, pixels.GetPixel(50, 50));
                AssertEqual(SKColors.Lime, pixels.GetPixel(95, 50));
            }
            long attempts = pool.Retained.DecodeAttempts;
            control.Apply(node with { RasterImage = recipe with { FitMode = XsrUiImageFitMode.Cover } });
            using (var pixels = DrawComposite(control))
            {
                AssertEqual(SKColors.Blue, pixels.GetPixel(20, 5));
                AssertEqual(SKColors.Blue, pixels.GetPixel(80, 95));
            }
            control.Apply(node with { RasterImage = recipe with { FitMode = XsrUiImageFitMode.Stretch } });
            using (var pixels = DrawComposite(control))
            {
                AssertEqual(SKColors.Red, pixels.GetPixel(5, 5));
                AssertEqual(SKColors.Blue, pixels.GetPixel(50, 5));
                AssertEqual(SKColors.Lime, pixels.GetPixel(95, 95));
            }
            control.Apply(node with { RasterImage = recipe with { ImageOpacity = .5 } });
            using (var pixels = DrawComposite(control))
            {
                AssertEqual(SKColors.White, pixels.GetPixel(50, 5)); // Carrier color retains its own opacity.
                SKColor translucent = pixels.GetPixel(50, 50);
                AssertTrue(translucent.Red is >= 127 and <= 128 && translucent.Green is >= 127 and <= 128
                    && translucent.Blue == 255 && translucent.Alpha == 255);
            }
            control.Apply(node with { RasterImage = recipe with { FitMode = XsrUiImageFitMode.Cover, ImageOpacity = 0 } });
            using (var pixels = DrawComposite(control)) AssertEqual(SKColors.White, pixels.GetPixel(50, 50));
            AssertEqual(attempts, pool.Retained.DecodeAttempts); // Placement/opacity changes reuse the bounded decode.
            AssertEqual(1, pool.Retained.Leases);
            AssertEqual(256, AvaloniaUiSceneNodeControl.PreviewWidth(1600, 400, 200, 100, 1));
            AssertEqual(512, AvaloniaUiSceneNodeControl.PreviewWidth(1600, 400, 200, 100, 1, XsrUiImageFitMode.Cover));
            AssertEqual(1024, AvaloniaUiSceneNodeControl.PreviewWidth(4096, 1024, 10000, 10000, 2, XsrUiImageFitMode.Stretch));
        }
        finally { control.ReleasePresentation(); pool.TrimIdle(); }
        AssertEqual(0, pool.Retained.Leases); AssertEqual(0L, pool.Retained.Bytes);
        Console.WriteLine("PASS: real raster contain/cover/stretch, carrier color, image-only opacity and bounded decode reuse");
    }
}
