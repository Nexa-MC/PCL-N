using SkiaSharp;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>Explicit image processing effect; owned caller validates destination and admission.</summary>
public static class AvaloniaUiScreenshotCodec
{
    public static byte[] Crop(ReadOnlyMemory<byte> source, int x, int y, int width, int height)
    {
        if (source.Length is < 33 or > 16 * 1024 * 1024 || width <= 0 || height <= 0 || x < 0 || y < 0
            || (long)width * height > 32 * 1024 * 1024) throw new InvalidDataException("截图裁剪范围超出预算。");
        using var data = SKData.CreateCopy(source.Span);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("截图无法解码。");
        if (codec.Info.Width > 8192 || codec.Info.Height > 8192 || (long)codec.Info.Width * codec.Info.Height > 32 * 1024 * 1024
            || (long)x + width > codec.Info.Width || (long)y + height > codec.Info.Height)
            throw new InvalidDataException("截图裁剪范围无效。");
        using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("截图无法解码。");
        using var cropped = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(cropped))
            canvas.DrawBitmap(bitmap, new SKRect(x, y, x + width, y + height), new SKRect(0, 0, width, height));
        using var image = SKImage.FromBitmap(cropped);
        using var output = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new IOException("截图无法编码。");
        if (output.Size > 16 * 1024 * 1024) throw new IOException("裁剪结果超出大小预算。");
        return output.ToArray();
    }
}
