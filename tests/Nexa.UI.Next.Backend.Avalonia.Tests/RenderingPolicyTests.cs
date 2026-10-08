using Avalonia;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void LauncherRenderingPolicySelectsActualNativeOptionsAndPreservesRestartCapture()
    {
        var accelerated = new AvaloniaUiRenderingConfiguration(false);
        AssertFalse(accelerated.HardwareAccelerationDisabled);
        AssertEqual(Win32RenderingMode.AngleEgl, accelerated.Windows.RenderingMode[0]);
        AssertEqual(Win32RenderingMode.Software, accelerated.Windows.RenderingMode[^1]);
        AssertTrue(accelerated.Windows.RenderingMode.Contains(Win32RenderingMode.Wgl));
        AssertEqual(Win32CompositionMode.WinUIComposition, accelerated.Windows.CompositionMode[0]);
        AssertEqual(X11RenderingMode.Glx, accelerated.Linux.RenderingMode[0]);
        AssertEqual(X11RenderingMode.Software, accelerated.Linux.RenderingMode[^1]);
        var software = new AvaloniaUiRenderingConfiguration(true);
        bool supported = OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
        AssertEqual(supported, software.HardwareAccelerationDisabled);
        if (supported)
        {
            AssertEqual(1, software.Windows.RenderingMode.Count);
            AssertEqual(Win32RenderingMode.Software, software.Windows.RenderingMode.Single());
            AssertEqual(Win32CompositionMode.RedirectionSurface, software.Windows.CompositionMode.Single());
            AssertEqual(1, software.Linux.RenderingMode.Count);
            AssertEqual(X11RenderingMode.Software, software.Linux.RenderingMode.Single());
        }
        else
        {
            AssertEqual(accelerated.Windows.RenderingMode[0], software.Windows.RenderingMode[0]);
            AssertEqual(accelerated.Linux.RenderingMode[0], software.Linux.RenderingMode[0]);
        }
        _ = new AvaloniaUiRenderingConfiguration(false);
        AssertEqual(supported, software.HardwareAccelerationDisabled);
    }
}
