using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>Two bounded product images owned by one native window/session; returned images are borrowed.</summary>
internal sealed class AvaloniaUiBrandImages : IDisposable
{
    private readonly Bitmap _light;
    private readonly Bitmap _dark;
    private WindowIcon? _lightIcon;
    private WindowIcon? _darkIcon;
    internal bool IsDisposed { get; private set; }

    private AvaloniaUiBrandImages(Stream light, Stream dark)
    {
        _light = new Bitmap(light);
        try { _dark = new Bitmap(dark); }
        catch { _light.Dispose(); throw; }
    }

    internal static AvaloniaUiBrandImages Load(Stream light, Stream dark)
    {
        ArgumentNullException.ThrowIfNull(light);
        ArgumentNullException.ThrowIfNull(dark);
        return new(light, dark);
    }

    internal static AvaloniaUiBrandImages? TryLoadProduct()
    {
        using Stream? light = AvaloniaUiShellHost.TryOpenProductAsset("Nexa.Desktop.Assets.Brand.neon-n-light.png");
        using Stream? dark = AvaloniaUiShellHost.TryOpenProductAsset("Nexa.Desktop.Assets.Brand.neon-n-dark.png");
        if (light is null || dark is null) return null;
        try { return Load(light, dark); }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        { return null; }
    }

    internal Bitmap Resolve(bool isDark)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return isDark ? _dark : _light;
    }

    internal WindowIcon ResolveIcon(bool isDark)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return isDark ? _darkIcon ??= new WindowIcon(_dark) : _lightIcon ??= new WindowIcon(_light);
    }

    internal static bool ResolveDark(XsrUiThemeMode mode, ThemeVariant actualTheme) => mode switch
    {
        XsrUiThemeMode.Light => false,
        XsrUiThemeMode.Dark => true,
        XsrUiThemeMode.System => actualTheme == ThemeVariant.Dark,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _lightIcon = null; _darkIcon = null;
        _light.Dispose(); _dark.Dispose();
    }
}
