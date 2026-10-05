namespace Nexa.UI.Next.Backend.Avalonia;

internal sealed partial class AvaloniaUiSceneNodeControl
{
    private static readonly XsrUiVisualStyleSnapshot OwnedToggleSurface = new XsrUiVisualStyle
    {
        Background = new(255, 255, 255),
        Border = new(178, 189, 204),
    }.Snapshot();
    private static readonly XsrUiVisualStyleSnapshot OwnedToggleRail = new XsrUiVisualStyle
    {
        Background = new(218, 225, 234),
    }.Snapshot();
    private static readonly XsrUiVisualStyleSnapshot OwnedCapsuleHighlight = new XsrUiVisualStyle
    {
        Background = new(255, 255, 255, 245),
    }.Snapshot();

    private XsrUiColor ToggleFill(XsrUiVisualStyleSnapshot style)
    {
        XsrUiColor original = style.Border.Alpha > 0 ? style.Border : style.Foreground;
        XsrUiColorScheme scheme = _node.ColorScheme;
        var rgb = (original.Red, original.Green, original.Blue);
        if (rgb == (scheme.AccentText.Red, scheme.AccentText.Green, scheme.AccentText.Blue)
            || rgb == (scheme.AccentFill.Red, scheme.AccentFill.Green, scheme.AccentFill.Blue)
            || rgb is (11, 91, 203) or (19, 112, 243) or (28, 97, 210))
            return scheme.AccentFill with { Alpha = original.Alpha };
        // Explicit custom scene fill colors are already projected by the renderer and
        // must not be replaced merely because they are used by a toggle.
        return original;
    }
}
