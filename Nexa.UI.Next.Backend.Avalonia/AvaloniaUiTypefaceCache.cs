using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>
/// UI-thread cache of typefaces that the active backend can actually load. A platform
/// default can name an installed but unsupported font (for example a WOFF2 on Linux).
/// </summary>
internal sealed class AvaloniaUiTypefaceCache(
    Func<Typeface, bool> canLoad,
    Func<IEnumerable<FontFamily>> installedFamilies)
{
    private static readonly ConditionalWeakTable<FontManager, AvaloniaUiTypefaceCache> Caches = new();
    private static readonly FontFamily[] FallbackFamilies =
    [
        new("Noto Sans"), new("DejaVu Sans"), new("Liberation Sans"),
        new("Segoe UI"), new("Arial"), new("Helvetica Neue"), new("Helvetica"),
    ];
    private readonly Dictionary<(FontWeight Weight, FontStyle Style), Typeface> _typefaces = [];

    internal static Typeface GetDefault(FontWeight weight, FontStyle style = FontStyle.Normal) =>
        Caches.GetValue(FontManager.Current, static manager => new(
            typeface => manager.TryGetGlyphTypeface(typeface, out _),
            () => manager.SystemFonts)).Get(weight, style);

    internal Typeface Get(FontWeight weight, FontStyle style = FontStyle.Normal)
    {
        if (_typefaces.TryGetValue((weight, style), out Typeface typeface)) return typeface;

        typeface = new Typeface(FontFamily.Default, style, weight);
        if (!canLoad(typeface))
        {
            HashSet<FontFamily> attempted = [FontFamily.Default];
            foreach (FontFamily family in Candidates())
            {
                if (!attempted.Add(family)) continue;
                typeface = new Typeface(family, style, weight);
                if (canLoad(typeface)) return Cache(typeface);
            }

            throw new InvalidOperationException("No usable system font could be loaded for UI text.");
        }

        return Cache(typeface);

        Typeface Cache(Typeface resolved)
        {
            _typefaces.Add((weight, style), resolved);
            return resolved;
        }
    }

    private IEnumerable<FontFamily> Candidates()
    {
        foreach (FontFamily family in FallbackFamilies) yield return family;
        foreach (FontFamily family in installedFamilies()) yield return family;
    }
}
