using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Nexa.UI.Next;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiSceneSurface
{
    private ContextMenu? _contextMenu;
    internal ContextMenu? PresentedContextMenu => _contextMenu;

    internal bool ShowContextMenu(XsrUiPoint point, bool keyboard = false)
    {
        CommitScene();
        XsrUiEntityId target = keyboard ? _shell.Renderer.Focused : _shell.Renderer.HitTest(point);
        if (!target.IsAssigned) return false;
        _contextMenu?.Close();
        FontFamily family = AvaloniaUiTypefaceCache.GetDefault(FontWeight.Normal).FontFamily;
        _shell.Renderer.CancelPointerGesture();
        if (_scene?.Nodes.FirstOrDefault(node => node.Entity == target) is { TextInput: { } text } node)
        {
            if (!node.IsEnabled || !node.IsAccessible) return false;
            _shell.Renderer.Focus(target, showIndicator: keyboard);
            CommitScene();
            List<MenuItem> editItems = [];
            void Edit(string label, Action action, bool enabled)
            {
                MenuItem item = new() { Header = _shell.Renderer.LocalizeText(label), IsEnabled = enabled, FontFamily = family };
                item.Click += (_, _) =>
                {
                    CommitScene();
                    if (_disposed || _shell.Renderer.Focused != target
                        || _scene?.Nodes.Any(current => current.Entity == target && current.IsAccessible && current.IsEnabled) != true) return;
                    action(); CommitScene();
                };
                editItems.Add(item);
            }
            bool selection = text.SelectionStart != text.SelectionEnd;
            Edit("剪切", () => _ = TransferClipboard(Key.X, target), selection && !text.IsPassword);
            Edit("复制", () => _ = TransferClipboard(Key.C, target), selection && !text.IsPassword);
            Edit("粘贴", () => _ = TransferClipboard(Key.V, target), true);
            Edit("全选", () => _shell.Renderer.EditText(XsrUiTextEdit.SelectAll), text.DisplayText.Length > 0);
            _contextMenu = new() { ItemsSource = editItems };
        }
        else
        {
            XsrUiContextMenuSnapshot? menu = _shell.Renderer.ContextMenuFor(target);
            if (menu is null) return false;
            _contextMenu = new()
            {
                ItemsSource = menu.Items.Select(entry =>
                {
                    MenuItem item = new() { Header = entry.Label, IsEnabled = entry.IsEnabled, FontFamily = family };
                    item.Click += (_, _) =>
                    {
                        CommitScene();
                        if (!_disposed) _shell.Renderer.InvokeContextMenu(menu.Source, entry.Command);
                        CommitScene();
                    };
                    return item;
                }).ToArray(),
            };
        }
        _contextMenu.Placement = keyboard ? PlacementMode.Bottom : PlacementMode.Pointer;
        _contextMenu.FontFamily = family;
        _contextMenu.Open(this);
        return true;
    }
}
