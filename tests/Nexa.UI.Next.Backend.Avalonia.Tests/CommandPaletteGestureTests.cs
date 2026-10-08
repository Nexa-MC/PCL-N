using Avalonia.Input;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void CommandPaletteGesturesRequireExactKeysAndAdmittedCallbacks()
    {
        var platform = new AvaloniaUiPlatformActions();
        AssertFalse(platform.HandleCommandPaletteKey(Key.P, KeyModifiers.Control | KeyModifiers.Shift));
        int opened = 0, closed = 0;
        platform.CommandPaletteOpenRequested = () => { opened++; return true; };
        platform.CommandPaletteCloseRequested = () => { closed++; return true; };
        AssertTrue(platform.HandleCommandPaletteKey(Key.P, KeyModifiers.Control | KeyModifiers.Shift));
        AssertEqual(1, opened);
        foreach (var modifiers in new[] { KeyModifiers.None, KeyModifiers.Shift, KeyModifiers.Control,
            KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt, KeyModifiers.Meta | KeyModifiers.Shift })
            AssertFalse(platform.HandleCommandPaletteKey(Key.P, modifiers));
        AssertFalse(platform.HandleCommandPaletteKey(Key.Escape, KeyModifiers.Control));
        AssertTrue(platform.HandleCommandPaletteKey(Key.Escape, KeyModifiers.None)); AssertEqual(1, closed);
        platform.CommandPaletteCloseRequested = () => false;
        AssertFalse(platform.HandleCommandPaletteKey(Key.Escape, KeyModifiers.None));
        platform.CommandPaletteOpenRequested = () => false;
        AssertFalse(platform.HandleCommandPaletteKey(Key.P, KeyModifiers.Control | KeyModifiers.Shift));
    }
}
