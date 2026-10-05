namespace Nexa.UI.Next;

/// <summary>Persisted theme-mode encodings, independent of shell layout and native toolkit.</summary>
public enum XsrUiThemeMode { Light = 0, Dark = 1, System = 2 }

public enum XsrUiAccent { Blue = 0, Purple = 1, Green = 2, Orange = 3 }

/// <summary>The optional native color-preference edge; it never reads product settings.</summary>
public interface IXsrUiSystemAppearance
{
    bool? IsSystemDark { get; }
    event Action? SystemAppearanceChanged;
    void SetThemeMode(XsrUiThemeMode mode);
}

/// <summary>
/// Resolves the product's original light tokens into immutable scene colors. Arbitrary image,
/// graph and custom colors are not recolored; original components are never rewritten.
/// </summary>
public readonly record struct XsrUiColorScheme(bool IsDark, XsrUiAccent Accent = XsrUiAccent.Blue)
{
    public XsrUiColor AccentFill => Accent switch
    {
        XsrUiAccent.Purple => new(111, 66, 193),
        XsrUiAccent.Green => new(0, 119, 72),
        XsrUiAccent.Orange => new(173, 74, 0),
        _ => new(19, 112, 243),
    };

    public XsrUiColor AccentText => IsDark ? Accent switch
    {
        XsrUiAccent.Purple => new(190, 159, 255),
        XsrUiAccent.Green => new(94, 210, 159),
        XsrUiAccent.Orange => new(255, 180, 107),
        _ => new(99, 164, 255),
    } : Accent switch
    {
        XsrUiAccent.Purple => new(99, 49, 179),
        XsrUiAccent.Green => new(0, 107, 61),
        XsrUiAccent.Orange => new(155, 64, 0),
        _ => new(11, 91, 203),
    };

    public XsrUiVisualStyleSnapshot Project(XsrUiVisualStyleSnapshot source)
    {
        if (this == default || !source.IsDefined) return source;
        return source with
        {
            Background = Background(source.Background),
            Foreground = Foreground(source.Foreground),
            Border = Border(source.Border),
            Hover = Background(source.Hover),
        };
    }

    public XsrUiColor Foreground(XsrUiColor color)
    {
        if (color.Alpha == 0 || this == default) return color;
        if (IsAccentInk(color)) return Alpha(AccentText, color);
        if (!IsDark) return color;
        return (color.Red, color.Green, color.Blue) switch
        {
            (52, 61, 74) or (38, 47, 60) or (43, 51, 64) or (38, 49, 65) or (55, 65, 81) or (40, 48, 60)
                => Alpha(new(232, 237, 245), color),
            (122, 138, 153) or (96, 108, 124) or (113, 124, 140) or (112, 124, 138) or (91, 105, 122)
                or (94, 110, 130) or (144, 159, 181)
                => Alpha(new(167, 180, 198), color),
            // Status/evidence foregrounds retain their hue but gain dark-surface contrast.
            (190, 124, 0) or (188, 125, 42) => Alpha(new(255, 198, 107), color),
            (207, 47, 54) or (173, 48, 48) or (196, 64, 54) => Alpha(new(255, 132, 142), color),
            (57, 105, 69) or (34, 128, 84) or (40, 135, 90) => Alpha(new(136, 215, 151), color),
            _ => color,
        };
    }

    private XsrUiColor Background(XsrUiColor color)
    {
        if (color.Alpha == 0 || this == default) return color;
        var rgb = (color.Red, color.Green, color.Blue);
        if (rgb is (19, 112, 243) or (11, 91, 203) or (28, 97, 210) or (23, 110, 225) or (32, 110, 224))
            return Alpha(AccentFill, color);
        if (rgb is (224, 234, 253) or (207, 225, 254) or (213, 230, 253)
            or (229, 239, 255) or (214, 231, 255) or (244, 248, 255) or (237, 243, 253)
            or (231, 240, 255) or (239, 244, 251) or (220, 230, 244) or (115, 158, 220))
            return Alpha(AccentTint(), color);
        if (!IsDark) return color;
        return rgb switch
        {
            (251, 251, 251) => Alpha(new(24, 29, 37), color),
            (255, 255, 255) => Alpha(new(32, 38, 48), color),
            (243, 247, 252) => Alpha(new(28, 34, 43), color),
            (241, 245, 250) or (242, 245, 249) or (238, 242, 247) or (241, 244, 248) or (240, 244, 250)
                or (244, 246, 250) or (245, 246, 248) or (245, 247, 250) or (245, 248, 252)
                or (232, 236, 242) or (243, 246, 250) or (244, 247, 251) or (247, 249, 252)
                or (236, 240, 246) => Alpha(new(42, 49, 62), color),
            (227, 233, 242) or (218, 225, 235) or (218, 225, 234) or (224, 230, 238) or (228, 233, 240)
                => Alpha(new(62, 72, 88), color),
            (255, 249, 235) => Alpha(new(55, 43, 27), color),
            (255, 244, 245) or (255, 224, 224) => Alpha(new(58, 32, 40), color),
            (235, 244, 233) or (222, 238, 219) => Alpha(new(28, 49, 36), color),
            _ => color,
        };
    }

    private XsrUiColor Border(XsrUiColor color)
    {
        if (color.Alpha == 0 || this == default) return color;
        var rgb = (color.Red, color.Green, color.Blue);
        if (IsAccentInk(color)) return Alpha(AccentText, color);
        if (rgb is (224, 234, 253) or (184, 214, 255) or (149, 186, 239) or (128, 172, 239))
            return Alpha(IsDark ? AccentTint() : Mix(AccentFill, new(255, 255, 255), .65), color);
        if (!IsDark) return color;
        if (rgb is (178, 189, 204)) return Alpha(new(167, 180, 198), color);
        if (rgb is (245, 211, 137)) return Alpha(new(112, 86, 47), color);
        if (rgb is (244, 184, 188)) return Alpha(new(118, 64, 73), color);
        return Background(color);
    }

    private XsrUiColor AccentTint() => IsDark
        ? Mix(AccentFill, new(24, 29, 37), .76)
        : Mix(AccentFill, new(255, 255, 255), .86);

    private static bool IsAccentInk(XsrUiColor color) => (color.Red, color.Green, color.Blue)
        is (19, 112, 243) or (11, 91, 203) or (28, 97, 210) or (48, 87, 145);
    private static XsrUiColor Alpha(XsrUiColor replacement, XsrUiColor original) => replacement with { Alpha = original.Alpha };
    private static XsrUiColor Mix(XsrUiColor from, XsrUiColor to, double amount) => new(
        (byte)Math.Round(from.Red + (to.Red - from.Red) * amount),
        (byte)Math.Round(from.Green + (to.Green - from.Green) * amount),
        (byte)Math.Round(from.Blue + (to.Blue - from.Blue) * amount));
}
