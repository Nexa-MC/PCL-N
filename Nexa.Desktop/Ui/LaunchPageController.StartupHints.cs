using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class LaunchPageController
{
    internal void SetStartupHintsVisible(bool visible)
    {
        if (_disposed || !_launchingEntities.TryGetValue("LaunchingHintBox", out var entity)
            || _shell.Tree.GetComponent<XsrUiElement>(entity) is not { } element || element.IsVisible == visible) return;
        element.IsVisible = visible;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
}
