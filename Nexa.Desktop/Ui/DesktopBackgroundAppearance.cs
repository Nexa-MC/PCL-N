using System.Buffers;
using System.Globalization;
using Nexa.Core.Media;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

/// <summary>One immutable configuration shared by the local PNG and decoded video carriers.</summary>
internal sealed record DesktopBackgroundAppearance(XsrUiImageFitMode FitMode, XsrUiColor? Color, double Opacity)
{
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");
    internal static DesktopBackgroundAppearance Default { get; } = new(XsrUiImageFitMode.Cover, null, 1);

    internal static DesktopBackgroundAppearance Read(IReadOnlyDictionary<string, string?> values)
    {
        XsrUiImageFitMode fit = values.GetValueOrDefault("appearance.background-fit") switch
        {
            "contain" => XsrUiImageFitMode.Contain,
            "stretch" => XsrUiImageFitMode.Stretch,
            _ => XsrUiImageFitMode.Cover,
        };
        int opacity = int.TryParse(values.GetValueOrDefault("appearance.background-opacity"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int parsed) ? Math.Clamp(parsed, 0, 100) : 100;
        return new(fit, ParseColor(values.GetValueOrDefault("appearance.background-color") ?? "auto"), opacity / 100.0);
    }

    internal static XsrUiColor? ParseColor(string text)
    {
        if (text == "auto") return null;
        if (text.Length != 7 || text[0] != '#' || text.AsSpan(1).ContainsAnyExcept(HexDigits)
            || !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            throw new InvalidDataException("背景颜色必须采用 auto 或 #RRGGBB。");
        return new XsrUiColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    internal XsrUiRasterImage CreateRaster(PngImage image) => new(image, [])
    {
        FitToBounds = true,
        FitMode = FitMode,
        AspectRatio = (double)image.Width / image.Height,
        ImageOpacity = Opacity,
    };
}

/// <summary>UI-thread projection preserves the latest static image underneath an active video.</summary>
internal sealed class DesktopBackgroundPresentation
{
    private readonly XsrUiTree _tree;
    private readonly XsrUiEntityId _carrier;
    private readonly XsrUiColor _originalColor;
    private readonly XsrUiRasterImage? _originalImage;
    private PngImage? _staticImage, _videoFrame;
    private DesktopBackgroundAppearance _appearance = DesktopBackgroundAppearance.Default;

    private DesktopBackgroundPresentation(XsrUiTree tree, XsrUiEntityId carrier)
    {
        _tree = tree; _carrier = carrier;
        _originalColor = tree.GetComponent<XsrUiVisualStyle>(carrier)!.Background;
        _originalImage = tree.GetComponent<XsrUiRasterImage>(carrier);
    }

    internal static DesktopBackgroundPresentation GetOrCreate(XsrUiTree tree, XsrUiEntityId carrier)
    {
        if (tree.GetComponent<DesktopBackgroundPresentation>(carrier) is { } current) return current;
        var created = new DesktopBackgroundPresentation(tree, carrier);
        tree.SetComponent(carrier, created);
        return created;
    }

    internal void SetStatic(PngImage? image, DesktopBackgroundAppearance appearance)
    {
        _staticImage = image; _appearance = appearance; Present();
    }

    internal void SetVideo(PngImage frame, DesktopBackgroundAppearance appearance)
    {
        _videoFrame = frame; _appearance = appearance; Present();
    }

    internal void SetAppearance(DesktopBackgroundAppearance appearance)
    {
        _appearance = appearance; Present();
    }

    internal void ClearVideo() { _videoFrame = null; Present(); }

    internal void ResetStatic()
    {
        _staticImage = null; _appearance = DesktopBackgroundAppearance.Default; Present();
    }

    private void Present()
    {
        PngImage? image = _videoFrame ?? _staticImage;
        _tree.SetComponent(_carrier, image is null ? _originalImage : _appearance.CreateRaster(image));
        var style = _tree.GetComponent<XsrUiVisualStyle>(_carrier)!;
        XsrUiColor color = _appearance.Color ?? _originalColor;
        if (style.Background != color)
        { style.Background = color; _tree.MarkDirty(_carrier, XsrUiDirtyKinds.Paint); }
    }
}
