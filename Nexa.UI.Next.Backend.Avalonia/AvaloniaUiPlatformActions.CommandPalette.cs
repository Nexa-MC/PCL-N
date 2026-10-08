using Avalonia.Input;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiPlatformActions
{
    /// <summary>Returns true only when the product admitted opening its command palette.</summary>
    public Func<bool>? CommandPaletteOpenRequested { get; set; }

    /// <summary>Returns true only when an open product palette consumed Escape.</summary>
    public Func<bool>? CommandPaletteCloseRequested { get; set; }

    internal bool HandleCommandPaletteKey(Key key, KeyModifiers modifiers)
    {
        if (key == Key.P && modifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            return CommandPaletteOpenRequested?.Invoke() ?? false;
        return key == Key.Escape && modifiers == KeyModifiers.None
            && (CommandPaletteCloseRequested?.Invoke() ?? false);
    }
}
