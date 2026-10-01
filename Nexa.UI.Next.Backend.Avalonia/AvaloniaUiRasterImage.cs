using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Nexa.UI.Next;

namespace Nexa.UI.Next.Backend.Avalonia;

internal sealed partial class AvaloniaUiSceneNodeControl
{
    private Bitmap? _rasterBitmap;
    private string? _rasterKey;
    private bool _rasterFit;
    private int _rasterWidth;
    internal bool HasDecodedRaster => _rasterBitmap is not null;
    internal Bitmap? DecodedRaster => _rasterBitmap;

    private void UpdateRaster(XsrUiRasterImage? raster, double width, double height)
    {
        string? key = raster?.Image.Key;
        bool fit = raster?.FitToBounds == true;
        int decodeWidth = raster is null ? 0 : raster.Image.Width;
        if (raster is not null && fit)
        {
            double density = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            decodeWidth = PreviewWidth(raster.Image.Width, raster.Image.Height, width, height, density);
        }
        if (_rasterKey == key && _rasterFit == fit && _rasterWidth == decodeWidth) return;
        _rasterBitmap?.Dispose(); _rasterBitmap = null;
        _rasterKey = key; _rasterFit = fit; _rasterWidth = decodeWidth;
        if (raster is null) return;
        try
        {
            // PngImage owns an array. Keep the stream read-only and avoid another encoded copy.
            if (!MemoryMarshal.TryGetArray(raster.Image.Bytes, out ArraySegment<byte> bytes)) return;
            using MemoryStream stream = new(bytes.Array!, bytes.Offset, bytes.Count, writable: false);
            Bitmap bitmap = fit ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality) : new(stream);
            if (raster.FitToBounds ? bitmap.PixelSize.Width > 1024 || bitmap.PixelSize.Height > 1024
                : bitmap.PixelSize.Width != raster.Image.Width || bitmap.PixelSize.Height != raster.Image.Height) bitmap.Dispose();
            else _rasterBitmap = bitmap;
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        { /* Malformed pixels keep the embedded source; image decode never breaks scene commit. */ }
    }

    internal static int PreviewWidth(int sourceWidth, int sourceHeight, double width, double height, double density)
    {
        int maximum = Math.Max(1, (int)(sourceWidth * Math.Min(1d, 1024d / Math.Max(sourceWidth, sourceHeight))));
        double scale = Math.Min(width / sourceWidth, height / sourceHeight) * density;
        int required = double.IsFinite(scale) ? (int)Math.Clamp(Math.Ceiling(sourceWidth * scale), 1, maximum) : maximum;
        int bucket = 32;
        while (bucket < required) bucket *= 2;
        return Math.Min(bucket, maximum);
    }

    internal void ReleasePresentation()
    {
        StopCaret();
        AvaloniaUiMotion.CancelAll(this);
        _rasterBitmap?.Dispose(); _rasterBitmap = null; _rasterKey = null; _rasterWidth = 0;
    }

    private bool DrawRaster(DrawingContext context, Rect bounds)
    {
        // DPI can change without changing the immutable scene node.
        UpdateRaster(_node.RasterImage, bounds.Width, bounds.Height);
        if (_rasterBitmap is not { } bitmap || _node.RasterImage is not { } raster) return false;
        if (raster.FitToBounds)
        {
            double scale = Math.Min(bounds.Width / raster.Image.Width, bounds.Height / raster.Image.Height);
            double width = raster.Image.Width * scale, height = raster.Image.Height * scale;
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
                context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height),
                    new Rect(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height));
            return true;
        }
        double size = Math.Min(bounds.Width, bounds.Height);
        double x = bounds.X + (bounds.Width - size) / 2, y = bounds.Y + (bounds.Height - size) / 2;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        {
            foreach (XsrUiImageLayer layer in raster.Layers)
                context.DrawImage(bitmap, new Rect(layer.Source.X, layer.Source.Y, layer.Source.Width, layer.Source.Height),
                    new Rect(x + layer.Destination.X * size, y + layer.Destination.Y * size, layer.Destination.Width * size, layer.Destination.Height * size));
        }
        return true;
    }
}
