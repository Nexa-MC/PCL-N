using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void VerifyNativeContextMenusPreserveLeftClickAndPasswordRules()
    {
        XsrUiIntentBuffer intents = new();
        var shell = XsrUiShellComposer.Compose(new XsrStateStoreBuilder().Build(), intentSink: intents);
        shell.Renderer.ReducedMotion = true;
        var row = shell.Tree.Create("context-target"); shell.Tree.Attach(row, shell.Content);
        shell.Tree.SetComponent(row, new XsrUiElement { Height = 44 });
        shell.Tree.SetComponent(row, new XsrUiText("Target"));
        shell.Tree.SetComponent(row, new XsrUiInput { Focusable = true, Clickable = true });
        shell.Tree.SetComponent(row, new XsrUiCommandBinding(XsrSemanticId.Parse("ui.test.left")));
        var menuCommand = XsrSemanticId.Parse("ui.test.context");
        shell.Tree.SetComponent(row, new XsrUiContextMenu([new("Menu action", menuCommand)]));
        using AvaloniaUiSceneSurface surface = new(shell);
        Window window = new() { Content = surface, Width = 500, Height = 300 };
        try
        {
            window.Show(); surface.Measure(new(500, 300)); surface.Arrange(new(0, 0, 500, 300)); surface.CommitScene();
            var node = surface.Scene!.Nodes.Single(candidate => candidate.Entity == row);
            var point = new Point(node.Rect.X + 10, node.Rect.Y + 10);
            window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
            AssertEqual(0, intents.Count);
            AssertFalse(surface.Scene.Nodes.Single(candidate => candidate.Entity == row).IsPressed);
            AssertTrue(surface.PresentedContextMenu?.IsOpen == true);
            MenuItem action = ((IEnumerable<MenuItem>)surface.PresentedContextMenu!.ItemsSource!).Single();
            action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            AssertEqual(menuCommand, intents.Drain().Single().Command);
            shell.Tree.Destroy(row);
            action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); AssertEqual(0, intents.Count);
            surface.PresentedContextMenu.Close();

            var password = shell.Tree.Create("context-password"); shell.Tree.Attach(password, shell.Content);
            shell.Tree.SetComponent(password, new XsrUiElement { Height = 44 });
            shell.Tree.SetComponent(password, new XsrUiInput { Focusable = true, Clickable = true });
            shell.Tree.SetComponent(password, new XsrUiTextInput { IsPassword = true });
            shell.Renderer.SetTextInputValue(password, "secret"); shell.Renderer.Focus(password);
            shell.Renderer.EditText(XsrUiTextEdit.SelectAll); surface.CommitScene();
            AssertTrue(surface.ShowContextMenu(default, keyboard: true));
            var items = ((IEnumerable<MenuItem>)surface.PresentedContextMenu!.ItemsSource!).ToArray();
            AssertFalse(items[0].IsEnabled); AssertFalse(items[1].IsEnabled);
            AssertTrue(items[2].IsEnabled); AssertTrue(items[3].IsEnabled);
            AssertTrue(shell.Renderer.CopySelectedText() is null);
        }
        finally { surface.PresentedContextMenu?.Close(); window.Close(); }
    }
}
