using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiShellWindow
{
    private AvaloniaUiBrandImages? _brandImages;
    private Bitmap? _fallbackBrandImage;
    private bool? _committedBrandDark;
    private bool? _presentedBrandDark;

    private void InitializeBranding(Stream? fallback)
    {
        PropertyChanged += OnBrandThemeChanged;
        if (AvaloniaUiBrandImages.TryLoadProduct() is { } images)
            SetOwnedBrandImages(images);
        else if (fallback is not null)
        {
            _fallbackBrandImage = new Bitmap(fallback);
            _closeIcon = _fallbackBrandImage;
            Icon = new WindowIcon(_fallbackBrandImage);
        }
    }

    /// <summary>Transfers ownership; production loads only embedded artwork, tests can supply the same assets explicitly.</summary>
    internal void SetOwnedBrandImages(AvaloniaUiBrandImages images)
    {
        ArgumentNullException.ThrowIfNull(images);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(_brandImages, images)) return;
        AvaloniaUiBrandImages? previous = _brandImages;
        Bitmap? previousFallback = _fallbackBrandImage;
        _brandImages = images;
        _fallbackBrandImage = null;
        _presentedBrandDark = null;
        ApplyBrandTheme(_committedBrandDark ?? ActualThemeVariant == ThemeVariant.Dark);
        previous?.Dispose(); previousFallback?.Dispose();
    }

    private void OnBrandThemeChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        // Before the first immutable scene, use the actual native preference. Once the
        // scene exists it is authoritative, including explicit Light on a dark OS.
        if (!_disposed && args.Property == ActualThemeVariantProperty && _committedBrandDark is null)
            ApplyBrandTheme(ActualThemeVariant == ThemeVariant.Dark);
    }

    private void ApplyCommittedBranding(XsrUiScene scene)
    {
        if (_disposed) return;
        _committedBrandDark = scene.Nodes.FirstOrDefault(node => node.Entity == _shell.Root).ColorScheme.IsDark;
        ApplyBrandTheme(_committedBrandDark.Value);
    }

    private void ApplyBrandTheme(bool isDark)
    {
        if (_brandImages is null || _presentedBrandDark == isDark) return;
        Bitmap? previous = _closeIcon;
        _closeIcon = _brandImages.Resolve(isDark);
        Icon = _brandImages.ResolveIcon(isDark);
        _presentedBrandDark = isDark;
        // An already running close decoration borrows the current image too. Scene raster
        // controls and user-provided logos are separate and are never touched here.
        if (_root is not null && previous is not null)
            foreach (Image image in _root.Children.OfType<Image>())
                if (ReferenceEquals(image.Source, previous)) image.Source = _closeIcon;
    }

    private void DisposeBranding()
    {
        PropertyChanged -= OnBrandThemeChanged;
        if (_root is not null)
            foreach (Image image in _root.Children.OfType<Image>())
                if (ReferenceEquals(image.Source, _closeIcon)) image.Source = null;
        _closeIcon = null;
        _brandImages?.Dispose(); _brandImages = null;
        _fallbackBrandImage?.Dispose(); _fallbackBrandImage = null;
    }
}
