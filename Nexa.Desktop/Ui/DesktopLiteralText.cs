using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal static class DesktopLiteralText
{
    internal static XsrUiEntityId Preserve(XsrUiTree tree, XsrUiEntityId entity)
    {
        if (tree.GetComponent<XsrUiText>(entity) is { } text) text.Localize = false;
        if (tree.GetComponent<XsrUiSemantic>(entity) is { } semantic) semantic.Localize = false;
        return entity;
    }
}
