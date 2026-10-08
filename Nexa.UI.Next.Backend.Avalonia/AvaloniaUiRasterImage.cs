using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Nexa.Core.Media;
using Nexa.UI.Next;

namespace Nexa.UI.Next.Backend.Avalonia;

internal sealed partial class AvaloniaUiSceneNodeControl
{
    // Fixed, one-pixel process-lifetime lighting textures; not decoded account media.
    private static readonly Lazy<Bitmap> HighlightShade = new(() => ShadePixel(0x10ffffff));
    private static readonly Lazy<Bitmap> BottomShade = new(() => ShadePixel(0x3a000000));
    private static readonly Lazy<Bitmap> BackShade = new(() => ShadePixel(0x20000000));
    private static readonly Lazy<Bitmap> SideShade = new(() => ShadePixel(0x14000000));
    private readonly AvaloniaUiRasterPool _rasterPool;
    private AvaloniaUiRasterPool.Lease? _rasterLease;
    private Dictionary<string, AvaloniaUiRasterPool.Lease>? _rasterLayerLeases;
    private List<PngImage>? _rasterLayerSources;
    private HashSet<string>? _rasterLayerFailures;
    private XsrUiRasterImage? _rasterRecipe;
    private string? _rasterKey;
    private bool _rasterFit;
    private int _rasterWidth;
    private bool _rasterPrimaryFailed;
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
            decodeWidth = PreviewWidth(raster.Image.Width, raster.Image.Height, width, height, density, raster.FitMode);
        }
        if (ReferenceEquals(_rasterRecipe, raster) && _rasterKey == key && _rasterFit == fit && _rasterWidth == decodeWidth
            && (!_rasterCapacityBlocked || _rasterAttemptRevision == _rasterPool.Revision)) return;
        bool samePrimary = _rasterKey == key && _rasterFit == fit && _rasterWidth == decodeWidth;
        if (!samePrimary)
        {
            _rasterLease?.Dispose(); _rasterLease = null;
            _rasterPrimaryFailed = false;
        }
        if (!ReferenceEquals(_rasterRecipe, raster)) ReconcileLayerRasters(raster);
        _rasterRecipe = raster;
        _rasterKey = key; _rasterFit = fit; _rasterWidth = decodeWidth;
        _rasterCapacityBlocked = false;
        if (raster is null) return;
        if (_rasterLease is null && !_rasterPrimaryFailed)
        {
            _rasterLease = _rasterPool.Acquire(raster.Image, fit, decodeWidth, out _rasterCapacityBlocked, out _rasterAttemptRevision);
            _rasterPrimaryFailed = _rasterLease is null && !_rasterCapacityBlocked;
        }
        if (_rasterLayerSources is { } sources)
        {
            foreach (PngImage image in sources)
            {
                if (_rasterLayerLeases?.ContainsKey(image.Key) == true || _rasterLayerFailures?.Contains(image.Key) == true) continue;
                var lease = _rasterPool.Acquire(image, false, image.Width, out bool blocked, out _rasterAttemptRevision);
                _rasterCapacityBlocked |= blocked;
                if (lease is not null) (_rasterLayerLeases ??= new(StringComparer.Ordinal)).Add(image.Key, lease);
                else if (!blocked) (_rasterLayerFailures ??= new(StringComparer.Ordinal)).Add(image.Key);
            }
        }
        // Only missing resources are retried. Keeping successful leases prevents two
        // capacity-blocked controls from invalidating each other's retry revision.
        _rasterAttemptRevision = _rasterPool.Revision;
    }

    private void ReconcileLayerRasters(XsrUiRasterImage? raster)
    {
        if (raster is null || raster.FitToBounds)
        {
            ReleaseLayerRasters();
            return;
        }
        List<PngImage>? sources = null;
        for (int index = 0; index < Math.Min(128, raster.Layers.Count); index++)
        {
            if (raster.Layers[index].SourceImage is not { } image || image.Key == raster.Image.Key) continue;
            sources ??= new(7);
            if (ContainsSource(sources, image.Key)) continue;
            if (sources.Count >= 7) break;
            sources.Add(image);
        }
        if (_rasterLayerSources is { } previous)
        {
            foreach (PngImage image in previous)
            {
                if (sources is not null && ContainsSource(sources, image.Key)) continue;
                if (_rasterLayerLeases is { } leases && leases.Remove(image.Key, out var lease)) lease.Dispose();
                _rasterLayerFailures?.Remove(image.Key);
            }
        }
        _rasterLayerSources = sources;
    }

    private static bool ContainsSource(List<PngImage> sources, string key)
    {
        foreach (PngImage image in sources)
            if (image.Key == key) return true;
        return false;
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

    internal static int PreviewWidth(int sourceWidth, int sourceHeight, double width, double height, double density,
        XsrUiImageFitMode fitMode = XsrUiImageFitMode.Contain)
    {
        int maximum = Math.Max(1, (int)(sourceWidth * Math.Min(1d, 1024d / Math.Max(sourceWidth, sourceHeight))));
        double scale = (fitMode is XsrUiImageFitMode.Cover or XsrUiImageFitMode.Stretch
            ? Math.Max(width / sourceWidth, height / sourceHeight)
            : Math.Min(width / sourceWidth, height / sourceHeight)) * density;
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
        ReleaseLayerRasters(); _rasterRecipe = null;
        _rasterPrimaryFailed = false; _rasterCapacityBlocked = false;
    }

    internal void SetRasterPresentationEnabled(bool enabled)
    {
        if (_rasterPresentationEnabled == enabled) return;
        _rasterPresentationEnabled = enabled;
        _rasterLease?.Dispose(); _rasterLease = null; _rasterKey = null; _rasterWidth = 0;
        ReleaseLayerRasters(); _rasterRecipe = null;
        _rasterPrimaryFailed = false; _rasterCapacityBlocked = false;
        if (enabled && !_presentationReleased) InvalidateVisual();
    }

    private bool DrawRaster(DrawingContext context, Rect bounds)
    {
        // DPI can change without changing the immutable scene node.
        UpdateRaster(_node.RasterImage, bounds.Width, bounds.Height);
        if (DecodedRaster is not { } bitmap || _node.RasterImage is not { } raster) return false;
        using var imageOpacity = context.PushOpacity(double.IsFinite(raster.ImageOpacity) ? Math.Clamp(raster.ImageOpacity, 0, 1) : 1);
        if (raster.FitToBounds)
        {
            double scale = raster.FitMode == XsrUiImageFitMode.Cover
                ? Math.Max(bounds.Width / raster.Image.Width, bounds.Height / raster.Image.Height)
                : Math.Min(bounds.Width / raster.Image.Width, bounds.Height / raster.Image.Height);
            double width = raster.FitMode == XsrUiImageFitMode.Stretch ? bounds.Width : raster.Image.Width * scale;
            double height = raster.FitMode == XsrUiImageFitMode.Stretch ? bounds.Height : raster.Image.Height * scale;
            using var clip = context.PushClip(bounds);
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
                context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height),
                    new Rect(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height));
            return true;
        }
        double aspect = raster.AspectRatio is > 0 and < 100 && double.IsFinite(raster.AspectRatio) ? raster.AspectRatio : 1;
        double canvasHeight = Math.Min(bounds.Width / aspect, bounds.Height), canvasWidth = canvasHeight * aspect;
        double x = bounds.X + (bounds.Width - canvasWidth) / 2, y = bounds.Y + (bounds.Height - canvasHeight) / 2;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        {
            for (int index = 0; index < Math.Min(16, raster.BackgroundEllipses.Count); index++)
            {
                XsrUiImageEllipse ellipse = raster.BackgroundEllipses[index];
                XsrUiRect r = ellipse.Bounds;
                if (!ValidRect(r)) continue;
                context.DrawEllipse(new SolidColorBrush(Color.FromUInt32(ellipse.Argb)), null,
                    new Point(x + (r.X + r.Width / 2) * canvasWidth, y + (r.Y + r.Height / 2) * canvasHeight),
                    r.Width * canvasWidth / 2, r.Height * canvasHeight / 2);
            }
            for (int index = 0; index < Math.Min(128, raster.Layers.Count); index++)
            {
                XsrUiImageLayer layer = raster.Layers[index];
                PngImage image = layer.SourceImage ?? raster.Image;
                Bitmap? sourceBitmap = image.Key == raster.Image.Key ? bitmap
                    : _rasterLayerLeases?.GetValueOrDefault(image.Key)?.Bitmap;
                if (sourceBitmap is null || !ValidSource(layer.Source, image)) continue;
                Rect source = new(layer.Source.X, layer.Source.Y, layer.Source.Width, layer.Source.Height);
                if (layer.Transform is { } t)
                {
                    if (!ValidTransform(t)) continue;
                    Matrix transform = new(t.M11 * canvasWidth, t.M12 * canvasHeight, t.M21 * canvasWidth,
                        t.M22 * canvasHeight, x + t.OffsetX * canvasWidth, y + t.OffsetY * canvasHeight);
                    using (context.PushTransform(transform)) DrawLayer(context, sourceBitmap, source, new(0, 0, 1, 1), layer.Shade);
                }
                else if (ValidRect(layer.Destination))
                    DrawLayer(context, sourceBitmap, source, new(x + layer.Destination.X * canvasWidth, y + layer.Destination.Y * canvasHeight,
                        layer.Destination.Width * canvasWidth, layer.Destination.Height * canvasHeight), layer.Shade);
            }
        }
        return true;
    }

    private void ReleaseLayerRasters()
    {
        if (_rasterLayerLeases is { } leases)
        {
            foreach (var lease in leases.Values) lease.Dispose();
            leases.Clear();
        }
        _rasterLayerSources = null;
        _rasterLayerFailures?.Clear();
    }

    private static void DrawLayer(DrawingContext context, Bitmap bitmap, Rect source, Rect destination, XsrUiImageShade shade)
    {
        Bitmap? tint = shade switch
        {
            XsrUiImageShade.Highlight => HighlightShade.Value,
            XsrUiImageShade.Bottom => BottomShade.Value,
            XsrUiImageShade.Back => BackShade.Value,
            XsrUiImageShade.Side => SideShade.Value,
            _ => null,
        };
        if (tint is null) { context.DrawImage(bitmap, source, destination); return; }
        // Isolate this face before SourceAtop, so a translucent texture neither shades
        // the preceding face nor acquires opaque pixels in its transparent regions.
        using (context.PushOpacityMask(Brushes.White, destination))
        {
            context.DrawImage(bitmap, source, destination);
            using (context.PushRenderOptions(new RenderOptions { BitmapBlendingMode = BitmapBlendingMode.SourceAtop }))
                context.DrawImage(tint, new Rect(0, 0, 1, 1), destination);
        }
    }

    private static WriteableBitmap ShadePixel(uint argb)
    {
        WriteableBitmap bitmap = new(new(1, 1), new(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var pixels = bitmap.Lock();
        System.Runtime.InteropServices.Marshal.WriteInt32(pixels.Address, unchecked((int)argb));
        return bitmap;
    }

    private static bool ValidRect(XsrUiRect r) => double.IsFinite(r.X) && double.IsFinite(r.Y)
        && double.IsFinite(r.Width) && double.IsFinite(r.Height) && r.Width > 0 && r.Height > 0
        && Math.Abs(r.X) <= 2 && Math.Abs(r.Y) <= 2 && r.Width <= 4 && r.Height <= 4;
    private static bool ValidSource(XsrUiRect r, PngImage image) => double.IsFinite(r.X) && double.IsFinite(r.Y)
        && double.IsFinite(r.Width) && double.IsFinite(r.Height) && r.X >= 0 && r.Y >= 0 && r.Width > 0 && r.Height > 0
        && r.X + r.Width <= image.Width && r.Y + r.Height <= image.Height;
    private static bool ValidTransform(XsrUiImageTransform t) => double.IsFinite(t.M11) && double.IsFinite(t.M12)
        && double.IsFinite(t.M21) && double.IsFinite(t.M22) && double.IsFinite(t.OffsetX) && double.IsFinite(t.OffsetY)
        && Math.Abs(t.M11) <= 4 && Math.Abs(t.M12) <= 4 && Math.Abs(t.M21) <= 4 && Math.Abs(t.M22) <= 4
        && Math.Abs(t.OffsetX) <= 2 && Math.Abs(t.OffsetY) <= 2
        && Math.Abs(t.M11 * t.M22 - t.M12 * t.M21) > 1e-12;
}
