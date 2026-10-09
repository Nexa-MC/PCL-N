using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>
/// The synchronous host's compact, passive startup card. Product startup uses the interactive
/// readiness window; both paths share the same theme-aware artwork and quiet presentation.
/// </summary>
public sealed class AvaloniaSplashWindow : Window
{
    private readonly AvaloniaUiStartupCard _card;
    private AvaloniaUiStartupAppearance _appearance = new();

    public AvaloniaSplashWindow(Stream iconStream)
    {
        ArgumentNullException.ThrowIfNull(iconStream);
        Width = 400;
        Height = 238;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];
        _card = new AvaloniaUiStartupCard(static text => text, fallback: iconStream);
        Content = _card;
        PropertyChanged += OnThemeChanged;
        UpdateBrand();
    }

    internal void SetAppearance(AvaloniaUiStartupAppearance appearance)
    {
        _appearance = appearance;
        RequestedThemeVariant = appearance.ThemeMode switch
        { XsrUiThemeMode.Light => ThemeVariant.Light, XsrUiThemeMode.Dark => ThemeVariant.Dark, _ => ThemeVariant.Default };
        UpdateBrand();
    }

    private void OnThemeChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    { if (args.Property == ActualThemeVariantProperty) UpdateBrand(); }

    private void UpdateBrand()
    {
        bool dark = AvaloniaUiBrandImages.ResolveDark(_appearance.ThemeMode, ActualThemeVariant);
        _card.ApplyAppearance(dark, _appearance.ProductVersion);
        Icon = _card.ProductIcon;
        TransparencyBackgroundFallback = new SolidColorBrush(dark ? Color.FromRgb(13, 26, 36) : Color.FromRgb(248, 252, 255));
    }

    protected override void OnClosed(EventArgs e)
    {
        PropertyChanged -= OnThemeChanged;
        _card.Dispose();
        base.OnClosed(e);
    }
}
