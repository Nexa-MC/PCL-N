namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void WorldEditLeaseRejectsNonMacBeforeLoadingNativeLibraries()
    {
        if (OperatingSystem.IsMacOS()) return;
        bool rejected = false;
        try { using var lease = AvaloniaUiWorldEditLease.Acquire(Path.GetFullPath("session.lock")); }
        catch (PlatformNotSupportedException) { rejected = true; }
        AssertTrue(rejected);
    }
}
