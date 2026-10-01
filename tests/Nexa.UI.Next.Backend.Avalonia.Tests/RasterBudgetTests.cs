using Avalonia.Media.Imaging;
using Nexa.Core.Media;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void VerifySharedRasterBudget()
    {
        PngImage blue = RasterPixels(SkiaSharp.SKColors.Blue), red = RasterPixels(SkiaSharp.SKColors.Red), green = RasterPixels(SkiaSharp.SKColors.Green);
        AvaloniaUiRasterPool bytes = new(1024, 10);
        var blue1 = AssertNotNull(bytes.Acquire(blue, false, 8, out _, out _));
        var blue2 = AssertNotNull(bytes.Acquire(blue, false, 8, out _, out _));
        var red1 = AssertNotNull(bytes.Acquire(red, false, 8, out _, out _));
        AssertTrue(ReferenceEquals(blue1.Bitmap, blue2.Bitmap));
        AssertEqual(1024L, bytes.Retained.Bytes); AssertEqual(3, bytes.Retained.Leases);
        AssertTrue(bytes.Acquire(green, false, 8, out bool full, out _) is null && full);
        blue1.Dispose(); blue1.Dispose();
        AssertTrue(bytes.Acquire(green, false, 8, out full, out _) is null && full);
        blue2.Dispose();
        using (var green1 = AssertNotNull(bytes.Acquire(green, false, 8, out _, out _)))
        {
            AssertEqual(1L, bytes.Retained.DisposedBitmaps);
            AssertEqual(new global::Avalonia.PixelSize(8, 8), red1.Bitmap.PixelSize);
            bytes.TrimIdle(); // The two visible leases must survive a trim.
            AssertEqual(2, bytes.Retained.Entries);
        }
        red1.Dispose(); bytes.TrimIdle();
        AssertEqual(0L, bytes.Retained.Bytes); AssertEqual(0, bytes.Retained.Entries); AssertEqual(0, bytes.Retained.Leases);

        // An independent entry bound must evict the least recently used idle bitmap.
        AvaloniaUiRasterPool count = new(1_048_576, 2);
        Bitmap retainedBlue;
        using (var lease = AssertNotNull(count.Acquire(blue, false, 8, out _, out _))) retainedBlue = lease.Bitmap;
        using (AssertNotNull(count.Acquire(red, false, 8, out _, out _))) { }
        using (var lease = AssertNotNull(count.Acquire(blue, false, 8, out _, out _))) AssertTrue(ReferenceEquals(retainedBlue, lease.Bitmap));
        using (AssertNotNull(count.Acquire(green, false, 8, out _, out _))) { }
        AssertEqual(2, count.Retained.Entries); AssertEqual(1L, count.Retained.DisposedBitmaps);
        using (var lease = AssertNotNull(count.Acquire(blue, false, 8, out _, out _))) AssertTrue(ReferenceEquals(retainedBlue, lease.Bitmap));
        using (AssertNotNull(count.Acquire(red, false, 8, out _, out _))) { }
        AssertEqual(4L, count.Retained.DecodeAttempts);
        count.TrimIdle(); AssertEqual(4L, count.Retained.DisposedBitmaps);

        AvaloniaUiRasterPool undersized = new(511, 10);
        AssertTrue(undersized.Acquire(blue, false, 8, out full, out _) is null && full);
        AssertEqual(0L, undersized.Retained.DecodeAttempts);
        VerifyRasterVisibilityAndRetry(blue, red);
        Console.WriteLine("PASS: shared raster leases, byte/count budgets, LRU, visibility, decode failure and capacity retry");
    }

    private static void VerifyRasterVisibilityAndRetry(PngImage blue, PngImage red)
    {
        AvaloniaUiRasterPool pool = new(512, 4);
        AvaloniaUiSceneNodeControl first = new(_ => { }, _ => { }, () => true, rasterPool: pool);
        AvaloniaUiSceneNodeControl second = new(_ => { }, _ => { }, () => true, rasterPool: pool);
        XsrUiTree tree = new();
        XsrUiSceneNode visible = Node(tree.Create("raster-budget"), XsrUiSemanticRole.Image) with
        { Rect = new(0, 0, 8, 8), RasterImage = new(blue, []) };
        try
        {
            first.Apply(visible);
            second.Apply(visible with { RasterImage = new(red, []) });
            AssertTrue(first.HasDecodedRaster); AssertFalse(second.HasDecodedRaster);
            long attempts = pool.Retained.DecodeAttempts;
            using var target = new RenderTargetBitmap(new(16, 16));
            second.Measure(new(8, 8)); second.Arrange(new(0, 0, 8, 8));
            for (int i = 0; i < 10; i++) target.Render(second);
            AssertEqual(attempts, pool.Retained.DecodeAttempts);
            first.ReleasePresentation();
            second.Apply(second.Node); // Same node may retry after the capacity release.
            AssertTrue(second.HasDecodedRaster); AssertEqual(1, pool.Retained.Leases);
            first.Measure(new(8, 8)); first.Arrange(new(0, 0, 8, 8)); target.Render(first);
            AssertFalse(first.HasDecodedRaster); // A late draw must not resurrect retired pixels.
            second.Apply(second.Node with { ClipRect = new(8, 0, 8, 8) });
            AssertFalse(second.HasDecodedRaster); AssertEqual(0, pool.Retained.Leases);
            second.Apply(second.Node with { ClipRect = new(3, 3, 8, 8) });
            AssertTrue(second.HasDecodedRaster);
            second.Apply(second.Node with { ClipRect = null, Rect = new(100, 0, 8, 8) }, new(100, 100));
            AssertFalse(second.HasDecodedRaster);
            second.Apply(second.Node with { Rect = new(99, 0, 8, 8) }, new(100, 100));
            AssertTrue(second.HasDecodedRaster);
            second.Apply(second.Node with { PresentationOpacity = 0 }, new(100, 100));
            AssertFalse(second.HasDecodedRaster);
            second.Apply(second.Node with { PresentationOpacity = 1, Rect = new(0, 0, 0, 8) });
            AssertFalse(second.HasDecodedRaster);

            PngImage malformed = AssertNotNull(PngImage.TryCreate(blue.Bytes.Span[..33]));
            second.Apply(visible with { RasterImage = new(malformed, []) });
            AssertFalse(second.HasDecodedRaster); attempts = pool.Retained.DecodeAttempts;
            for (int i = 0; i < 10; i++) { second.Apply(second.Node); target.Render(second); }
            AssertEqual(attempts, pool.Retained.DecodeAttempts);
            AssertEqual(0L, pool.Retained.Bytes); AssertEqual(0, pool.Retained.Leases);
            second.Apply(visible);
            AssertTrue(second.HasDecodedRaster);
            second.SetRasterPresentationEnabled(false); pool.TrimIdle();
            AssertEqual(0L, pool.Retained.Bytes); attempts = pool.Retained.DecodeAttempts;
            second.Apply(visible with { RasterImage = new(red, []) }); target.Render(second);
            AssertFalse(second.HasDecodedRaster); AssertEqual(attempts, pool.Retained.DecodeAttempts);
            second.SetRasterPresentationEnabled(true); target.Render(second);
            AssertTrue(second.HasDecodedRaster); AssertEqual(red.Key, second.Node.RasterImage!.Image.Key);
        }
        finally { first.ReleasePresentation(); second.ReleasePresentation(); pool.TrimIdle(); }
        AssertEqual(0L, pool.Retained.Bytes); AssertEqual(0, pool.Retained.Leases);
    }

    private static PngImage RasterPixels(SkiaSharp.SKColor color)
    {
        using var pixels = new SkiaSharp.SKBitmap(8, 8);
        pixels.Erase(color);
        using var image = SkiaSharp.SKImage.FromBitmap(pixels);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return AssertNotNull(PngImage.TryCreate(encoded.ToArray()));
    }
}
