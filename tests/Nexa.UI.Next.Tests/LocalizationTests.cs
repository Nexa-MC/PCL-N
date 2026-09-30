using Nexa.Xsr.State;

namespace Nexa.UI.Next.Tests;

internal static partial class Program
{
    private static void LocalizationRemeasuresWithoutChangingInputOrLiteralContent()
    {
        XsrUiTree tree = new();
        var root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        var caption = tree.Create("caption");
        tree.SetComponent(caption, new XsrUiElement { HorizontalAlignment = XsrUiAlignment.Start });
        tree.SetComponent(caption, new XsrUiText("设置"));
        tree.SetComponent(caption, new XsrUiSemantic(XsrUiSemanticRole.Button, "设置"));
        tree.Attach(caption, root);
        var literal = tree.Create("literal");
        tree.SetComponent(literal, new XsrUiText("设置") { Localize = false });
        tree.SetComponent(literal, new XsrUiSemantic(XsrUiSemanticRole.Text, "设置") { Localize = false });
        tree.Attach(literal, root);
        var input = tree.Create("input");
        tree.SetComponent(input, new XsrUiElement { Width = 240, Height = 40 });
        tree.SetComponent(input, new XsrUiInput { Focusable = true });
        tree.SetComponent(input, new XsrUiTextInput { Placeholder = "搜索版本" });
        tree.Attach(input, root);
        var renderer = new XsrUiRenderer(tree, new XsrStateStoreBuilder().Build());
        renderer.SetRoot(root);
        renderer.SetTextInputValue(input, "设置 {0} / 用户内容");
        renderer.Render(); renderer.Focus(input);
        var original = renderer.Render().Nodes.Single(node => node.Entity == caption);
        var temporary = tree.Create("temporary");
        renderer.SetRoot(temporary); renderer.Render();
        renderer.TextLocalizer = source => source switch { "设置" => "Settings", "搜索版本" => "Search versions", _ => source };
        renderer.SetRoot(root);
        var translated = renderer.Render();
        var label = translated.Nodes.Single(node => node.Entity == caption);
        AssertEqual("Settings", label.Text); AssertEqual("Settings", label.Label);
        AssertTrue(label.Rect.Width > original.Rect.Width);
        var user = translated.Nodes.Single(node => node.Entity == literal);
        AssertEqual("设置", user.Text); AssertEqual("设置", user.Label);
        var draft = translated.Nodes.Single(node => node.Entity == input).TextInput!.Value;
        AssertEqual("Search versions", draft.Placeholder);
        AssertEqual("设置 {0} / 用户内容", draft.DisplayText);
        AssertEqual(input, renderer.Focused);
        AssertEqual(draft.DisplayText.Length, draft.SelectionEnd);
        renderer.TextLocalizer = null;
        AssertEqual(original.Rect.Width, renderer.Render().Nodes.Single(node => node.Entity == caption).Rect.Width);
        AssertEqual("设置", tree.GetComponent<XsrUiText>(caption)!.Content);
    }
}
