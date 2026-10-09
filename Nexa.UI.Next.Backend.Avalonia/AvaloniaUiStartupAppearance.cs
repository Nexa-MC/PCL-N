namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>Finite startup presentation preferences supplied by the product composition root.</summary>
public sealed record AvaloniaUiStartupAppearance(XsrUiThemeMode ThemeMode = XsrUiThemeMode.System,
    bool ReducedMotion = false, string ProductVersion = "2.0.0.alpha.6")
{
    internal bool SuppressMotion => ReducedMotion;

    internal void Validate()
    {
        if (ThemeMode is not (XsrUiThemeMode.System or XsrUiThemeMode.Light or XsrUiThemeMode.Dark))
            throw new ArgumentOutOfRangeException(nameof(ThemeMode));
        ArgumentException.ThrowIfNullOrWhiteSpace(ProductVersion);
        if (ProductVersion.Length > 80 || ProductVersion.Any(char.IsControl))
            throw new ArgumentException("The startup product version must be a bounded display value.", nameof(ProductVersion));
    }
}
