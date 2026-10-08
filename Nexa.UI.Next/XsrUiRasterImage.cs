using Nexa.Core.Media;

namespace Nexa.UI.Next;

/// <summary>One source-pixel crop placed into a normalized destination rectangle.</summary>
public readonly record struct XsrUiImageLayer(XsrUiRect Source, XsrUiRect Destination)
{
    /// <summary>Optional immutable source; otherwise use the recipe's primary image.</summary>
    public PngImage? SourceImage { get; init; }
    /// <summary>Maps a unit square into the normalized recipe canvas.</summary>
    public XsrUiImageTransform? Transform { get; init; }
    /// <summary>Optional surface lighting; preserves the source image's alpha.</summary>
    public XsrUiImageShade Shade { get; init; }
}

/// <summary>A fixed lighting palette with bounded native resources.</summary>
public enum XsrUiImageShade { None, Highlight, Bottom, Back, Side }

/// <summary>Placement of a complete image within its carrier, with cover cropped to the carrier.</summary>
public enum XsrUiImageFitMode { Contain, Cover, Stretch }

/// <summary>Normalized affine map, using row-vector matrix order.</summary>
public readonly record struct XsrUiImageTransform(double M11, double M12, double M21, double M22, double OffsetX, double OffsetY);

/// <summary>A solid normalized ellipse rendered behind the image layers.</summary>
public readonly record struct XsrUiImageEllipse(XsrUiRect Bounds, uint Argb);

/// <summary>Immutable encoded resource and draw recipe; no I/O or native bitmap crosses into UI.Next.</summary>
public sealed record XsrUiRasterImage(PngImage Image, IReadOnlyList<XsrUiImageLayer> Layers)
{
    /// <summary>Fit a complete local image into its bounds instead of composing square skin layers.</summary>
    public bool FitToBounds { get; init; }
    /// <summary>Complete-image placement; existing recipes retain proportional containment.</summary>
    public XsrUiImageFitMode FitMode { get; init; } = XsrUiImageFitMode.Contain;
    /// <summary>Logical width divided by height; existing square recipes use one.</summary>
    public double AspectRatio { get; init; } = 1;
    /// <summary>Only the image carrier is translucent; child content remains opaque.</summary>
    public double ImageOpacity { get; init; } = 1;
    public IReadOnlyList<XsrUiImageEllipse> BackgroundEllipses { get; init; } = [];
}
