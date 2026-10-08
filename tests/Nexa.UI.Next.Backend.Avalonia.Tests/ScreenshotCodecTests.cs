using SkiaSharp;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void ScreenshotCodecCropsActualPixelsAndPreservesSource()
    {
        using var source = new SKBitmap(5, 4);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++) source.SetPixel(x, y, new SKColor((byte)(x * 40), (byte)(y * 50), 70));
        using var encoded = source.Encode(SKEncodedImageFormat.Png, 100);
        byte[] bytes = encoded.ToArray(); byte[] original = (byte[])bytes.Clone();
        byte[] cropped = Nexa.UI.Next.Backend.Avalonia.AvaloniaUiScreenshotCodec.Crop(bytes, 1, 1, 3, 2);
        using var decoded = SKBitmap.Decode(cropped);
        AssertEqual(3, decoded.Width); AssertEqual(2, decoded.Height);
        for (int y = 0; y < decoded.Height; y++)
            for (int x = 0; x < decoded.Width; x++) AssertEqual(source.GetPixel(x + 1, y + 1), decoded.GetPixel(x, y));
        AssertTrue(bytes.AsSpan().SequenceEqual(original));
        bool rejected = false;
        try { _ = Nexa.UI.Next.Backend.Avalonia.AvaloniaUiScreenshotCodec.Crop(bytes, 4, 3, 2, 2); }
        catch (InvalidDataException) { rejected = true; }
        AssertTrue(rejected);
    }
}
