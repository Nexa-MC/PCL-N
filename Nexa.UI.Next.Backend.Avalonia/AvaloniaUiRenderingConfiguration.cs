using Avalonia;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>Typed restart-only presentation policy, captured before native initialization.</summary>
internal sealed class AvaloniaUiRenderingConfiguration
{
    internal static bool SupportsSoftwarePreference => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
    internal bool HardwareAccelerationDisabled { get; }
    internal Win32PlatformOptions Windows { get; }
    internal X11PlatformOptions Linux { get; }

    internal AvaloniaUiRenderingConfiguration(bool disableHardwareAcceleration)
    {
        HardwareAccelerationDisabled = disableHardwareAcceleration && SupportsSoftwarePreference;
        Windows = new Win32PlatformOptions
        {
            RenderingMode = HardwareAccelerationDisabled ? [Win32RenderingMode.Software]
                : [Win32RenderingMode.AngleEgl, Win32RenderingMode.Vulkan, Win32RenderingMode.Wgl, Win32RenderingMode.Software],
            CompositionMode = HardwareAccelerationDisabled ? [Win32CompositionMode.RedirectionSurface]
                : [Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface],
        };
        Linux = new X11PlatformOptions
        {
            RenderingMode = HardwareAccelerationDisabled ? [X11RenderingMode.Software]
                : [X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Vulkan, X11RenderingMode.Software],
        };
    }

    internal AppBuilder Apply(AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.With(Windows).With(Linux);
    }
}
