using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiPlatformActions : IXsrUiSystemAppearance
{
    private IPlatformSettings? _appearanceSettings;
    private XsrUiThemeMode _themeMode = XsrUiThemeMode.System;
    private int _systemDark = -1;

    /// <summary>Null denotes an unavailable platform preference; product System mode can use its fallback.</summary>
    public bool? IsSystemDark => Volatile.Read(ref _systemDark) switch { 0 => false, 1 => true, _ => null };
    public event Action? SystemAppearanceChanged;

    public void SetThemeMode(XsrUiThemeMode mode)
    {
        if (mode is not (XsrUiThemeMode.System or XsrUiThemeMode.Light or XsrUiThemeMode.Dark))
            throw new ArgumentOutOfRangeException(nameof(mode));
        _themeMode = mode;
        if (_owner is null) return;
        if (Dispatcher.UIThread.CheckAccess()) ApplyRequestedTheme();
        else Dispatcher.UIThread.Post(ApplyRequestedTheme);
    }

    private void AttachAppearance(TopLevel owner)
    {
        if (_appearanceSettings is not null) _appearanceSettings.ColorValuesChanged -= OnPlatformColorsChanged;
        _appearanceSettings = owner.GetPlatformSettings();
        if (_appearanceSettings is not null) _appearanceSettings.ColorValuesChanged += OnPlatformColorsChanged;
        ApplyRequestedTheme();
        ApplyWindowAppearance();
        if (owner is Window activatedWindow)
        {
            activatedWindow.Activated += (_, _) => PublishWindowActivity(true);
            activatedWindow.Deactivated += (_, _) => PublishWindowActivity(false);
            activatedWindow.KeyDown += OnMediaKey;
            PublishWindowActivity(activatedWindow.IsActive);
        }
        try { PublishSystemAppearance(_appearanceSettings?.GetColorValues()); }
        catch (Exception) { PublishSystemAppearance(null); }
        if (owner is Window window)
            window.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_owner, owner)) return;
                if (_appearanceSettings is not null) _appearanceSettings.ColorValuesChanged -= OnPlatformColorsChanged;
                _appearanceSettings = null;
            };
    }

    private void OnPlatformColorsChanged(object? sender, PlatformColorValues colors)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => { if (ReferenceEquals(sender, _appearanceSettings)) PublishSystemAppearance(colors); });
            return;
        }
        if (ReferenceEquals(sender, _appearanceSettings)) PublishSystemAppearance(colors);
    }

    private void PublishSystemAppearance(PlatformColorValues? colors)
    {
        int dark = colors?.ThemeVariant switch { PlatformThemeVariant.Dark => 1, PlatformThemeVariant.Light => 0, _ => -1 };
        if (Interlocked.Exchange(ref _systemDark, dark) == dark) return;
        SystemAppearanceChanged?.Invoke();
        // The product applies the preference in FramePreparing; a quiet window must get one
        // frame after an OS notification even when no durable setting was changed.
        if (_owner is AvaloniaUiShellWindow window) window.Surface.RequestCommit();
    }

    private void ApplyRequestedTheme()
    {
        if (_owner is null) return;
        _owner.RequestedThemeVariant = _themeMode switch
        {
            XsrUiThemeMode.Light => ThemeVariant.Light,
            XsrUiThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
