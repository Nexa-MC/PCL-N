using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Nexa.UI.Next;

namespace Nexa.UI.Next.Backend.Avalonia;

internal sealed partial class AvaloniaUiSceneNodeControl
{
    private readonly AvaloniaUiRasterPool _rasterPool;
    private AvaloniaUiRasterPool.Lease? _rasterLease;
    private string? _rasterKey;
    private bool _rasterFit;
    private int _rasterWidth;
    private bool _rasterCapacityBlocked;
    private long _rasterAttemptRevision;
    private bool _presentationReleased;
    private bool _rasterPresentationEnabled = true;
    private XsrUiSize? _rasterViewport;
    internal bool HasDecodedRaster => _rasterLease is not null;
    internal Bitmap? DecodedRaster => _rasterLease?.Bitmap;

    private void UpdateRaster(XsrUiRasterImage? raster, double width, double height)
    {
        if (_presentationReleased || !_rasterPresentationEnabled || !RasterVisible(_node, _rasterViewport)) raster = null;
        string? key = raster?.Image.Key;
        bool fit = raster?.FitToBounds == true;
        int decodeWidth = raster is null ? 0 : raster.Image.Width;
        if (raster is not null && fit)
        {
            double density = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            decodeWidth = PreviewWidth(raster.Image.Width, raster.Image.Height, width, height, density);
        }
        if (_rasterKey == key && _rasterFit == fit && _rasterWidth == decodeWidth
            && (!_rasterCapacityBlocked || _rasterAttemptRevision == _rasterPool.Revision)) return;
        _rasterLease?.Dispose(); _rasterLease = null;
        _rasterKey = key; _rasterFit = fit; _rasterWidth = decodeWidth;
        _rasterCapacityBlocked = false;
        if (raster is null) return;
        _rasterLease = _rasterPool.Acquire(raster.Image, fit, decodeWidth, out _rasterCapacityBlocked, out _rasterAttemptRevision);
    }

    private static bool RasterVisible(XsrUiSceneNode node, XsrUiSize? viewport)
    {
        XsrUiRect rect = node.Rect;
        if (!(node.PresentationOpacity > 0) || !double.IsFinite(rect.X) || !double.IsFinite(rect.Y)
            || !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height) || rect.Width <= 0 || rect.Height <= 0) return false;
        double left = rect.X, top = rect.Y, right = rect.X + rect.Width, bottom = rect.Y + rect.Height;
        if (node.ClipRect is { } clip)
        {
            left = Math.Max(left, clip.X); top = Math.Max(top, clip.Y);
            right = Math.Min(right, clip.X + clip.Width); bottom = Math.Min(bottom, clip.Y + clip.Height);
        }
        if (viewport is { } size)
        {
            left = Math.Max(left, 0); top = Math.Max(top, 0);
            right = Math.Min(right, size.Width); bottom = Math.Min(bottom, size.Height);
        }
        return right > left && bottom > top;
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
        _presentationReleased = true;
        _rasterLease?.Dispose(); _rasterLease = null; _rasterKey = null; _rasterWidth = 0;
    }

    internal void SetRasterPresentationEnabled(bool enabled)
    {
        if (_rasterPresentationEnabled == enabled) return;
        _rasterPresentationEnabled = enabled;
        _rasterLease?.Dispose(); _rasterLease = null; _rasterKey = null; _rasterWidth = 0;
        _rasterCapacityBlocked = false;
        if (enabled && !_presentationReleased) InvalidateVisual();
    }

    private bool DrawRaster(DrawingContext context, Rect bounds)
    {
        // DPI can change without changing the immutable scene node.
        UpdateRaster(_node.RasterImage, bounds.Width, bounds.Height);
        if (DecodedRaster is not { } bitmap || _node.RasterImage is not { } raster) return false;
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
