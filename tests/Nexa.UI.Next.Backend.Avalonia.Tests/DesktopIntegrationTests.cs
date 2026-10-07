using Avalonia.Controls;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void VerifyTrayCloseAndExplicitExitAdmission(AvaloniaUiShellWindow window, XsrUiShell shell)
    {
        using (var integration = new AvaloniaUiDesktopIntegration(window, () => { window.Show(); window.Activate(); }, () => { }, value => value))
        {
            // A native host without an icon must never hide an inaccessible window.
            integration.Apply(new(true, true, true));
            AssertFalse(integration.TrayAvailable);
            AssertTrue(window.HideToTrayRequested!());
            AssertTrue(window.IsVisible);
            AssertEqual(WindowState.Minimized, window.WindowState);
            window.WindowState = WindowState.Normal;
        }
        int restores = 0;
        window.StartupRevealCompleted += OnReveal;
        void OnReveal(object? sender, EventArgs args) => restores++;
        int hidden = 0, guarded = 0;
        var oldGuard = window.CloseGuard;
        window.HideToTrayRequested = () => { hidden++; window.Hide(); return true; };
        window.CloseGuard = () => { guarded++; return false; };
        try
        {
            window.RequestWindowClose();
            AssertEqual(1, hidden); AssertEqual(0, guarded); AssertFalse(window.IsVisible);
            window.Show(); window.Activate();
            AssertTrue(window.IsVisible); AssertEqual(0, restores);
            // Explicit tray exit must go through pending-install admission, never hide again.
            window.RequestClose();
            AssertEqual(1, hidden); AssertEqual(1, guarded); AssertTrue(window.IsVisible);
        }
        finally
        {
            window.CloseGuard = oldGuard; window.HideToTrayRequested = null;
            window.StartupRevealCompleted -= OnReveal;
        }
    }
}
