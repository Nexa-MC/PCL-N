using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using SkiaSharp;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void VerifyNativeAppearancePreferences(AvaloniaUiShellWindow window, XsrUiShell shell, AvaloniaUiSceneSurface surface)
    {
        var actions = new AvaloniaUiPlatformActions();
        actions.SetThemeMode(XsrUiThemeMode.Dark); // The persisted preference can precede attachment.
        actions.Attach(window);
        AssertEqual(ThemeVariant.Dark, window.RequestedThemeVariant);
        actions.SetThemeMode(XsrUiThemeMode.Light);
        AssertEqual(ThemeVariant.Light, window.RequestedThemeVariant);
        actions.SetThemeMode(XsrUiThemeMode.System);
        AssertEqual(ThemeVariant.Default, window.RequestedThemeVariant);
        bool invalidRejected = false;
        try { actions.SetThemeMode((XsrUiThemeMode)99); }
        catch (ArgumentOutOfRangeException) { invalidRejected = true; }
        AssertTrue(invalidRejected);
        AssertEqual(ThemeVariant.Default, window.RequestedThemeVariant);

        var previous = shell.Renderer.ColorScheme;
        try
        {
            shell.Renderer.ColorScheme = new(true, XsrUiAccent.Purple);
            surface.CommitScene();
            var scene = AssertNotNull(surface.Scene);
            var title = scene.Nodes.First(node => node.Role == XsrUiSemanticRole.TitleBar).VisualStyle;
            var root = scene.Nodes.First(node => node.Entity == shell.Root).VisualStyle;
            AssertTrue(scene.Nodes.All(node => node.ColorScheme == shell.Renderer.ColorScheme));
            AssertEqual(new XsrUiColor(111, 66, 193), title.Background);
            AssertEqual(new XsrUiColor(255, 255, 255), title.Foreground);
            AssertEqual(new XsrUiColor(24, 29, 37), root.Background);
            var nativeBackground = AssertNotNull(window.TransparencyBackgroundFallback as LinearGradientBrush);
            AssertEqual(Color.FromRgb(title.Background.Red, title.Background.Green, title.Background.Blue), nativeBackground.GradientStops[0].Color);
            AssertEqual(Color.FromRgb(root.Background.Red, root.Background.Green, root.Background.Blue), nativeBackground.GradientStops[2].Color);
        }
        finally
        {
            shell.Renderer.ColorScheme = previous;
            surface.CommitScene();
        }
        VerifyBackendAppearanceDrawingPixels();
        Console.WriteLine("PASS: native appearance applies pre-attachment mode and scene colors to opaque chrome");
    }

    private static void VerifyBackendAppearanceDrawingPixels()
    {
        var tree = new XsrUiTree();
        XsrUiEntityId entity = tree.Create("appearance-drawing");
        var source = new XsrUiVisualStyle { Background = new(240, 244, 250), Foreground = new(38, 49, 65), FontSize = 14 }.Snapshot();
        foreach (bool dark in new[] { false, true })
            foreach (var accent in Enum.GetValues<XsrUiAccent>())
            {
                var scheme = new XsrUiColorScheme(dark, accent);
                var input = Node(entity, XsrUiSemanticRole.TextInput) with
                {
                    Rect = new(0, 0, 180, 40),
                    ColorScheme = scheme,
                    VisualStyle = scheme.Project(source),
                    TextInput = new("", "Placeholder", false, 0, 0, ""),
                };
                using (var pixels = DrawAppearance(input))
                    AssertTrue(MaximumAppearanceContrast(pixels, new(12, 4, 150, 32), pixels.GetPixel(2, 2)) >= 4.5);
                using (var pixels = DrawAppearance(input with { IsFocused = true, TextInput = new("", "", false, 0, 0, "") }))
                {
                    // This edge is painted by the actual input focus-ring path.
                    AssertTrue(AppearancePixelContrast(pixels.GetPixel(20, 0), pixels.GetPixel(2, 2)) >= 4.5);
                    // An empty draft puts the freshly reset caret at the integer x=12,
                    // so this samples the caret itself without glyph antialiasing.
                    AssertTrue(AppearancePixelContrast(pixels.GetPixel(12, 25), pixels.GetPixel(2, 2)) >= 4.5);
                }
                using (var pixels = DrawAppearance(input with { Role = XsrUiSemanticRole.Button, TextInput = null, IsFocusVisible = true }))
                    AssertTrue(AppearancePixelContrast(pixels.GetPixel(20, 1), pixels.GetPixel(5, 5)) >= 4.5);

                var graph = input with
                {
                    Role = XsrUiSemanticRole.None,
                    TextInput = null,
                    VisualStyle = scheme.Project(new XsrUiVisualStyle { Background = new(247, 249, 252), Foreground = new(43, 51, 64) }.Snapshot()),
                    Graph = new([new(1, "Graph label", new(207, 121, 33), -50, 0, 5)], [], 1, 0, 0, 1, ""),
                };
                using (var pixels = DrawAppearance(graph))
                {
                    AssertTrue(MaximumAppearanceContrast(pixels, new(55, 12, 120, 26), pixels.GetPixel(2, 2)) >= 4.5);
                    // The selected graph node remains its content/evidence color.
                    AssertEqual(new SKColor(207, 121, 33), pixels.GetPixel(40, 20));
                }

                var toggleStyle = scheme.Project(new XsrUiVisualStyle { Border = new(11, 91, 203), Foreground = new(52, 61, 74) }.Snapshot());
                var checkbox = Node(entity, XsrUiSemanticRole.CheckBox) with
                {
                    ColorScheme = scheme,
                    VisualStyle = toggleStyle,
                    IsChecked = true,
                };
                using (var pixels = DrawAppearance(checkbox))
                {
                    SKColor fill = pixels.GetPixel(4, 15);
                    AssertTrue(HasAppearanceWhiteMark(pixels, new(5, 15, 15, 12)));
                    AssertTrue(AppearancePixelContrast(SKColors.White, fill) >= 4.5);
                }
                using (var pixels = DrawAppearance(checkbox with { Role = XsrUiSemanticRole.Switch }))
                {
                    AssertEqual(SKColors.White, pixels.GetPixel(60, 20));
                    AssertTrue(AppearancePixelContrast(pixels.GetPixel(60, 20), pixels.GetPixel(35, 20)) >= 4.5);
                }
                using (var pixels = DrawAppearance(checkbox with
                {
                    VisualStyle = new XsrUiVisualStyle { Border = new(78, 23, 92) }.Snapshot(),
                }))
                    AssertEqual(new SKColor(78, 23, 92), pixels.GetPixel(4, 15));

                var capsule = input with
                {
                    Role = XsrUiSemanticRole.Button,
                    TextInput = null,
                    ImageSource = "lucide/settings",
                    Text = "Theme",
                    CapsuleExpansionProgress = 1,
                    VisualStyle = scheme.Project(new XsrUiVisualStyle { Background = new(224, 234, 253), Foreground = new(11, 91, 203), HoverExpand = true }.Snapshot()),
                };
                using (var pixels = DrawAppearance(capsule))
                {
                    XsrUiColor ink = capsule.VisualStyle.Foreground;
                    var foreground = new SKColor(ink.Red, ink.Green, ink.Blue);
                    AssertTrue(AppearancePixelContrast(foreground, pixels.GetPixel(90, 3)) >= 4.5);
                    AssertTrue(AppearancePixelContrast(foreground, pixels.GetPixel(90, 36)) >= 4.5);
                }
                if (dark)
                {
                    var scrolled = input with
                    {
                        Role = XsrUiSemanticRole.None,
                        TextInput = null,
                        Rect = new(0, 0, 180, 120),
                        Scroll = new(0, 0, 180, 120, 180, 400, true),
                    };
                    using var pixels = DrawAppearance(scrolled);
                    // The existing 3-pixel rail spans x=173..176, so pixel 174 is
                    // its actual interior. Outside neighbors must stay unpainted.
                    SKColor background = pixels.GetPixel(2, 2);
                    SKColor thumb = pixels.GetPixel(174, 16), track = pixels.GetPixel(174, 80);
                    AssertEqual(background, pixels.GetPixel(172, 16));
                    AssertEqual(background, pixels.GetPixel(177, 16));
                    AssertTrue(thumb != background && track != background);
                    double againstBackground = AppearancePixelContrast(thumb, background), againstTrack = AppearancePixelContrast(thumb, track);
                    if (againstBackground < 3 || againstTrack < 3)
                        throw new InvalidOperationException($"Rendered dark scrollbar {accent}: background={background}, track={track}, thumb={thumb}; contrast background={againstBackground:F3}, track={againstTrack:F3}.");
                }
            }
        Console.WriteLine("PASS: rendered placeholder, graph ink/evidence, focus, checkbox/switch, custom fills, capsules and scrollbar contrast");
    }

    private static SKBitmap DrawAppearance(XsrUiSceneNode node)
    {
        var control = new AvaloniaUiSceneNodeControl(_ => { }, _ => { }, () => true);
        try
        {
            control.Apply(node);
            control.Measure(new(node.Rect.Width, node.Rect.Height));
            control.Arrange(new(0, 0, node.Rect.Width, node.Rect.Height));
            using var target = new RenderTargetBitmap(new((int)node.Rect.Width, (int)node.Rect.Height), new(96, 96));
            target.Render(control);
            using var stream = new MemoryStream();
            target.Save(stream, PngBitmapEncoderOptions.Default); stream.Position = 0;
            return AssertNotNull(SKBitmap.Decode(stream));
        }
        finally { control.ReleasePresentation(); }
    }

    private static double MaximumAppearanceContrast(SKBitmap pixels, Rect region, SKColor background)
    {
        double maximum = 1;
        for (int y = (int)region.Y; y < region.Bottom; y++)
            for (int x = (int)region.X; x < region.Right; x++)
                if (pixels.GetPixel(x, y).Alpha == 255)
                    maximum = Math.Max(maximum, AppearancePixelContrast(pixels.GetPixel(x, y), background));
        return maximum;
    }

    private static bool HasAppearanceWhiteMark(SKBitmap pixels, Rect region)
    {
        for (int y = (int)region.Y; y < region.Bottom; y++)
            for (int x = (int)region.X; x < region.Right; x++)
                if (pixels.GetPixel(x, y) == SKColors.White) return true;
        return false;
    }

    private static double AppearancePixelContrast(SKColor foreground, SKColor background)
    {
        static double Channel(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double Luminance(SKColor c) => .2126 * Channel(c.Red) + .7152 * Channel(c.Green) + .0722 * Channel(c.Blue);
        double fg = Luminance(foreground), bg = Luminance(background);
        return (Math.Max(fg, bg) + .05) / (Math.Min(fg, bg) + .05);
    }
}
