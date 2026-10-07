using Avalonia.Media.Imaging;
using Nexa.Core.Media;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using SkiaSharp;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void VerifyCompositeRasterDrawing()
    {
        PngImage blue = RasterPixels(SKColors.Blue), red = CompositeSplitPixels(), green = RasterPixels(SKColors.Lime);
        AvaloniaUiRasterPool pool = new(1_048_576, 16);
        var control = new AvaloniaUiSceneNodeControl(_ => { }, _ => { }, () => true, rasterPool: pool);
        XsrUiTree tree = new();
        XsrUiSceneNode node = Node(tree.Create("affine-preview"), XsrUiSemanticRole.Image) with
        { Rect = new(0, 0, 100, 100), ImageSource = null };
        var raster = new XsrUiRasterImage(blue,
        [
            new(new(0, 0, 8, 8), new(.1, .1, .8, .7)),
            new(new(0, 0, 4, 8), default) { SourceImage = red, Transform = new(.4, .2, .2, .4, .2, .1), Shade = XsrUiImageShade.Back },
            new(new(0, 0, 8, 8), new(.45, .35, .1, .1)) { SourceImage = green },
        ])
        { BackgroundEllipses = [new(new(.1, .85, .8, .1), 0xff00ff00)] };
        try
        {
            control.Apply(node with { RasterImage = raster });
            using (var pixels = DrawComposite(control))
            {
                AssertEqual(SKColors.Blue, pixels.GetPixel(20, 70));
                AssertEqual(SKColors.Lime, pixels.GetPixel(50, 40));
                SKColor face = pixels.GetPixel(30, 25);
                AssertTrue(face.Red is >= 220 and <= 225 && face.Green == 0 && face.Blue == 0 && face.Alpha == 255);
                AssertEqual(SKColors.Lime, pixels.GetPixel(50, 90));
                AssertEqual((byte)0, pixels.GetPixel(5, 90).Alpha);
            }
            AssertEqual(3, pool.Retained.Leases); AssertEqual(3L, pool.Retained.DecodeAttempts);
            for (int index = 0; index < 4; index++)
            {
                // A camera change replaces geometry without changing decoded PNG resources.
                control.Apply(node with { RasterImage = raster with { Layers = raster.Layers.Reverse().ToArray() } });
                using var pixels = DrawComposite(control);
                AssertEqual(3, pool.Retained.Leases); AssertEqual(3L, pool.Retained.DecodeAttempts);
            }
            var plain = new XsrUiRasterImage(blue, [new(new(0, 0, 8, 8), new(0, 0, 1, 1))]) { AspectRatio = .65 };
            control.Apply(node with { Rect = new(0, 0, 160, 100), RasterImage = plain });
            using (var pixels = DrawComposite(control))
            {
                AssertEqual(SKColors.Blue, pixels.GetPixel(80, 50));
                AssertEqual((byte)0, pixels.GetPixel(20, 50).Alpha);
                AssertEqual((byte)0, pixels.GetPixel(140, 50).Alpha);
            }
            AssertEqual(1, pool.Retained.Leases);
            control.Apply(node with { RasterImage = raster, PresentationOpacity = 0 }); AssertEqual(0, pool.Retained.Leases);
            control.Apply(node with { RasterImage = raster }); AssertEqual(3, pool.Retained.Leases);
            control.SetRasterPresentationEnabled(false); pool.TrimIdle(); AssertEqual(0L, pool.Retained.Bytes);
            using (DrawComposite(control)) { }
            AssertEqual(0, pool.Retained.Leases);
            control.SetRasterPresentationEnabled(true); using (DrawComposite(control)) { }
            AssertEqual(3, pool.Retained.Leases);
            VerifyCompositeMalformedLayers(control, node, blue, red, pool);
            VerifyCompositeShadePreservesAlpha(control, node);
            VerifyCompositeRecipeBounds(control, node, blue, pool);
            VerifyCompositeSecondaryCapacityRetry(blue, green);
            VerifyCompositeBlockedControlsRemainStable();
        }
        finally { control.ReleasePresentation(); pool.TrimIdle(); }
        AssertEqual(0, pool.Retained.Leases); AssertEqual(0L, pool.Retained.Bytes);
        Console.WriteLine("PASS: affine crop, depth, aspect, ground ellipse, alpha-safe shade, composite source leases and malformed geometry");
    }

    private static void VerifyCompositeMalformedLayers(AvaloniaUiSceneNodeControl control, XsrUiSceneNode node,
        PngImage blue, PngImage red, AvaloniaUiRasterPool pool)
    {
        PngImage malformed = AssertNotNull(PngImage.TryCreate(red.Bytes.Span[..33]));
        var safe = new XsrUiRasterImage(blue,
        [
            new(new(0, 0, 8, 8), new(0, 0, 1, 1)),
            new(new(0, 0, 8, 8), new(0, 0, 1, 1)) { SourceImage = malformed },
            new(new(7, 0, 8, 8), new(0, 0, 1, 1)) { SourceImage = red },
            new(new(0, 0, 8, 8), default) { SourceImage = red, Transform = new(double.NaN, 0, 0, 1, 0, 0) },
            new(new(0, 0, 8, 8), default) { SourceImage = red, Transform = new(double.MaxValue, 0, 0, 1, 0, 0) },
            new(new(0, 0, 8, 8), default) { SourceImage = red, Transform = new(0, 0, 0, 0, 0, 0) },
            new(new(0, 0, 8, 8), new(double.MaxValue, 0, 1, 1)) { SourceImage = red },
        ])
        { BackgroundEllipses = [new(new(double.NaN, 0, 1, 1), 0xffff0000)] };
        control.Apply(node with { RasterImage = safe });
        long attempts = pool.Retained.DecodeAttempts;
        for (int index = 0; index < 3; index++)
        {
            control.Apply(control.Node);
            using var pixels = DrawComposite(control);
            AssertEqual(SKColors.Blue, pixels.GetPixel(50, 50));
        }
        AssertEqual(attempts, pool.Retained.DecodeAttempts); // Failed source is not retried every frame.
        AssertTrue(control.HasDecodedRaster);
        control.Apply(node with { RasterImage = new(malformed, []), ImageSource = "pcl/avatar/steve" });
        using (var pixels = DrawComposite(control))
            AssertTrue(pixels.GetPixel(50, 50).Alpha > 0); // Existing embedded presentation fallback remains usable.
        AssertFalse(control.HasDecodedRaster); AssertEqual(0, pool.Retained.Leases);
    }

    private static void VerifyCompositeShadePreservesAlpha(AvaloniaUiSceneNodeControl control, XsrUiSceneNode node)
    {
        using var source = new SKBitmap(8, 8); source.Erase(SKColors.Transparent);
        for (int y = 0; y < 8; y++) for (int x = 4; x < 8; x++) source.SetPixel(x, y, new(255, 0, 0, 128));
        using var image = SKImage.FromBitmap(source); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        PngImage png = AssertNotNull(PngImage.TryCreate(encoded.ToArray()));
        control.Apply(node with { RasterImage = new(png, [new(new(0, 0, 8, 8), new(0, 0, 1, 1)) { Shade = XsrUiImageShade.Back }]) });
        using var pixels = DrawComposite(control);
        AssertEqual((byte)0, pixels.GetPixel(25, 50).Alpha);
        SKColor shaded = pixels.GetPixel(75, 50);
        AssertTrue(shaded.Alpha is >= 127 and <= 129 && shaded.Red is >= 219 and <= 225 && shaded.Green == 0 && shaded.Blue == 0);
    }

    private static void VerifyCompositeRecipeBounds(AvaloniaUiSceneNodeControl control, XsrUiSceneNode node,
        PngImage blue, AvaloniaUiRasterPool pool)
    {
        List<XsrUiImageLayer> layers = [new(new(0, 0, 8, 8), new(0, 0, 1, 1))];
        for (int index = 0; index < 8; index++)
            layers.Add(new(new(0, 0, 8, 8), new(0, 0, 1, 1))
            { SourceImage = RasterPixels(new((byte)(40 + index), 80, 120)) });
        control.Apply(node with { RasterImage = new(blue, layers.AsReadOnly()) });
        AssertEqual(8, pool.Retained.Leases); // Primary plus seven independent sources.
        using (var pixels = DrawComposite(control))
            AssertEqual(new SKColor(46, 80, 120), pixels.GetPixel(50, 50));

        PngImage excluded = RasterPixels(new SKColor(90, 110, 130));
        layers = [];
        for (int index = 0; index < 128; index++) layers.Add(new(new(0, 0, 8, 8), new(0, 0, 1, 1)));
        layers.Add(new(new(0, 0, 8, 8), new(0, 0, 1, 1)) { SourceImage = excluded });
        long attempts = pool.Retained.DecodeAttempts;
        control.Apply(node with { RasterImage = new(blue, layers.AsReadOnly()) });
        using (var pixels = DrawComposite(control)) AssertEqual(SKColors.Blue, pixels.GetPixel(50, 50));
        AssertEqual(attempts, pool.Retained.DecodeAttempts); // Layer 129 has neither drawing nor decode work.
        AssertEqual(1, pool.Retained.Leases);
    }

    private static void VerifyCompositeSecondaryCapacityRetry(PngImage blue, PngImage green)
    {
        AvaloniaUiRasterPool pool = new(1024, 2);
        PngImage blocker = RasterPixels(new SKColor(70, 90, 110));
        var held = AssertNotNull(pool.Acquire(blocker, false, 8, out _, out _));
        var control = new AvaloniaUiSceneNodeControl(_ => { }, _ => { }, () => true, rasterPool: pool);
        XsrUiTree tree = new();
        var node = Node(tree.Create("composite-capacity-retry"), XsrUiSemanticRole.Image) with
        {
            Rect = new(0, 0, 100, 100),
            ImageSource = null,
            RasterImage = new(blue,
            [
                new(new(0, 0, 8, 8), new(0, 0, 1, 1)),
                new(new(0, 0, 8, 8), new(0, 0, 1, 1)) { SourceImage = green },
            ]),
        };
        try
        {
            control.Apply(node);
            long attempts = pool.Retained.DecodeAttempts;
            for (int index = 0; index < 3; index++)
            {
                control.Apply(node);
                using var pixels = DrawComposite(control);
                AssertEqual(SKColors.Blue, pixels.GetPixel(50, 50));
            }
            AssertTrue(control.HasDecodedRaster); AssertEqual(attempts, pool.Retained.DecodeAttempts);
            held.Dispose();
            control.Apply(node); // A freed slot retries the secondary source without losing the primary.
            using (var pixels = DrawComposite(control)) AssertEqual(SKColors.Lime, pixels.GetPixel(50, 50));
            AssertEqual(attempts + 1, pool.Retained.DecodeAttempts); AssertEqual(2, pool.Retained.Leases);
            control.Apply(node with { ClipRect = new(100, 0, 100, 100) });
            AssertEqual(0, pool.Retained.Leases);
        }
        finally { held.Dispose(); control.ReleasePresentation(); pool.TrimIdle(); }
        AssertEqual(0L, pool.Retained.Bytes); AssertEqual(0, pool.Retained.Leases);
    }

    private static void VerifyCompositeBlockedControlsRemainStable()
    {
        PngImage blue = RasterPixels(SKColors.Blue), red = RasterPixels(SKColors.Red),
            yellow = RasterPixels(SKColors.Yellow), cyan = RasterPixels(SKColors.Cyan),
            green = RasterPixels(SKColors.Lime), orange = RasterPixels(SKColors.Orange);
        AvaloniaUiRasterPool pool = new(2048, 4);
        // Other visible previews have filled the cache before these two composite
        // previews acquire their shared, already resident textures.
        var heldBlue = AssertNotNull(pool.Acquire(blue, false, 8, out _, out _));
        var heldRed = AssertNotNull(pool.Acquire(red, false, 8, out _, out _));
        var heldYellow = AssertNotNull(pool.Acquire(yellow, false, 8, out _, out _));
        var heldCyan = AssertNotNull(pool.Acquire(cyan, false, 8, out _, out _));
        var first = new AvaloniaUiSceneNodeControl(_ => { }, _ => { }, () => true, rasterPool: pool);
        var second = new AvaloniaUiSceneNodeControl(_ => { }, _ => { }, () => true, rasterPool: pool);
        XsrUiTree tree = new();
        var firstNode = Node(tree.Create("composite-blocked-first"), XsrUiSemanticRole.Image) with
        { Rect = new(0, 0, 100, 100), ImageSource = null, RasterImage = CompositeRecipe(blue, red, green) };
        var secondNode = Node(tree.Create("composite-blocked-second"), XsrUiSemanticRole.Image) with
        { Rect = new(0, 0, 100, 100), ImageSource = null, RasterImage = CompositeRecipe(yellow, cyan, orange) };
        try
        {
            first.Apply(firstNode); second.Apply(secondNode);
            heldBlue.Dispose(); heldRed.Dispose(); heldYellow.Dispose(); heldCyan.Dispose();
            AssertEqual(4, pool.Retained.Leases);
            // A camera/layout recipe change retains the same decoded source set.
            firstNode = firstNode with { RasterImage = firstNode.RasterImage! with { AspectRatio = .65 } };
            first.Apply(firstNode);
            for (int index = 0; index < 4; index++) { first.Apply(firstNode); second.Apply(secondNode); }
            long revision = pool.Revision, attempts = pool.Retained.DecodeAttempts;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 100; index++) { first.Apply(firstNode); second.Apply(secondNode); }
            long steadyAllocations = GC.GetAllocatedBytesForCurrentThread() - allocated;
            AssertEqual(revision, pool.Revision);
            AssertEqual(attempts, pool.Retained.DecodeAttempts);
            AssertEqual(0L, steadyAllocations); // No lease replacements or retry scratch in unchanged frames.
            using (var pixels = DrawComposite(first)) AssertEqual(SKColors.Red, pixels.GetPixel(50, 50));
            using (var pixels = DrawComposite(second)) AssertEqual(SKColors.Cyan, pixels.GetPixel(50, 50));

            // A real source replacement releases only the retired texture, allowing
            // the replacement to enter the full cache without losing either primary.
            firstNode = firstNode with { RasterImage = CompositeRecipe(blue, green) };
            first.Apply(firstNode); second.Apply(secondNode);
            using (var pixels = DrawComposite(first)) AssertEqual(SKColors.Lime, pixels.GetPixel(50, 50));
            AssertEqual(4, pool.Retained.Leases);
            first.SetRasterPresentationEnabled(false);
            second.Apply(secondNode);
            using (var pixels = DrawComposite(second)) AssertEqual(SKColors.Orange, pixels.GetPixel(50, 50));
            AssertEqual(3, pool.Retained.Leases);
        }
        finally
        {
            heldBlue.Dispose(); heldRed.Dispose(); heldYellow.Dispose(); heldCyan.Dispose();
            first.ReleasePresentation(); second.ReleasePresentation(); pool.TrimIdle();
        }
        AssertEqual(0, pool.Retained.Leases); AssertEqual(0L, pool.Retained.Bytes);
    }

    private static XsrUiRasterImage CompositeRecipe(PngImage primary, params PngImage[] secondary)
    {
        List<XsrUiImageLayer> layers = [new(new(0, 0, 8, 8), new(0, 0, 1, 1))];
        foreach (PngImage image in secondary)
            layers.Add(new(new(0, 0, 8, 8), new(0, 0, 1, 1)) { SourceImage = image });
        return new(primary, layers.AsReadOnly());
    }

    private static SKBitmap DrawComposite(AvaloniaUiSceneNodeControl control)
    {
        double width = control.Node.Rect.Width, height = control.Node.Rect.Height;
        control.Measure(new(width, height)); control.Arrange(new(0, 0, width, height));
        using var target = new RenderTargetBitmap(new((int)width, (int)height), new(96, 96)); target.Render(control);
        using MemoryStream bytes = new(); target.Save(bytes, PngBitmapEncoderOptions.Default); bytes.Position = 0;
        return AssertNotNull(SKBitmap.Decode(bytes));
    }

    private static PngImage CompositeSplitPixels()
    {
        using var pixels = new SKBitmap(8, 8); pixels.Erase(SKColors.Red);
        for (int y = 0; y < 8; y++) for (int x = 4; x < 8; x++) pixels.SetPixel(x, y, SKColors.Yellow);
        using var image = SKImage.FromBitmap(pixels); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return AssertNotNull(PngImage.TryCreate(encoded.ToArray()));
    }
}
