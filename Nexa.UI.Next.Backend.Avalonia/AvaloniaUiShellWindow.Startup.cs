using Avalonia;
using Avalonia.Media.Imaging;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiShellWindow
{
    /// <summary>Warms the actual scene controls, fonts, geometry and decoded rasters while hidden.</summary>
    internal void PrepareStartupScene()
    {
        if (IsVisible) throw new InvalidOperationException("Startup preparation requires a hidden shell.");
        double width = Math.Max(1, Width - _restoredFrameInset * 2);
        double height = Math.Max(1, Height - _restoredFrameInset * 2);
        var viewport = new Size(width, height);
        _root.Measure(new Size(Width, Height));
        _root.Arrange(new Rect(0, 0, Width, Height));
        _surface.Measure(viewport);
        _surface.Arrange(new Rect(viewport));
        _surface.CommitScene();
        // Rendering real controls populates their retained FormattedText and raster caches.
        // The frame is bounded to the initial viewport and is never published to the screen.
        using var frame = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(width), (int)Math.Ceiling(height)),
            new Vector(96, 96));
        frame.Render(_surface);
    }

    internal void DiscardStartup()
    {
        _explicitCloseRequested = true;
        _closeAnimationStarted = true;
        CloseGuard = null;
        HideToTrayRequested = null;
        Close();
    }
}
