using Nexa.Xsr.State;

namespace Nexa.UI.Next.Tests;

internal static partial class Program
{
    private static void TextInputBoundsPreserveScriptTabsAndUnicodeWithoutChangingPasswords()
    {
        var tree = new XsrUiTree(); var root = tree.Create("root");
        tree.SetComponent(root, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        var normal = Add("ordinary", new XsrUiTextInput());
        var script = Add("script", new XsrUiTextInput { MaximumLength = 32768, PreserveTabs = true });
        var password = Add("password", new XsrUiTextInput { IsPassword = true, PreserveTabs = true });
        var renderer = new XsrUiRenderer(tree, new XsrStateStoreBuilder().Build()); renderer.SetRoot(root);
        renderer.SetTextInputValue(normal, "a\tb\r\nc");
        AssertEqual("abc", tree.GetComponent<XsrUiTextInput>(normal)!.ReadDraft());
        renderer.SetTextInputValue(script, "\tprintf '🚀'\r\n");
        AssertEqual("\tprintf '🚀'", tree.GetComponent<XsrUiTextInput>(script)!.ReadDraft());
        renderer.SetTextInputValue(normal, new string('x', 3000));
        AssertEqual(2048, tree.GetComponent<XsrUiTextInput>(normal)!.ReadDraft().Length);
        renderer.SetTextInputValue(script, new string('x', 32768) + "extra");
        AssertEqual(32768, tree.GetComponent<XsrUiTextInput>(script)!.ReadDraft().Length);
        renderer.SetTextInputValue(script, new string('x', 32767) + "🚀");
        AssertEqual(32767, tree.GetComponent<XsrUiTextInput>(script)!.ReadDraft().Length);
        renderer.Render(); renderer.Focus(script); renderer.EditText(XsrUiTextEdit.SelectAll);
        AssertTrue(renderer.InsertText("\techo preserved")); renderer.EditText(XsrUiTextEdit.SelectAll);
        AssertEqual("\techo preserved", renderer.CopySelectedText());
        renderer.SetTextInputValue(password, "a\tb\nc");
        AssertEqual("abc", tree.GetComponent<XsrUiTextInput>(password)!.ReadDraft());
        var snapshot = renderer.Render().Nodes.Single(node => node.Entity == password).TextInput!.Value;
        AssertEqual("•••", snapshot.DisplayText);
        renderer.Focus(password); renderer.EditText(XsrUiTextEdit.SelectAll); AssertTrue(renderer.CopySelectedText() is null);

        Nexa.UI.Next.XsrUiEntityId Add(string name, XsrUiTextInput input)
        {
            var entity = tree.Create(name); tree.Attach(entity, root);
            tree.SetComponent(entity, new XsrUiElement { Width = 300, Height = 36 });
            tree.SetComponent(entity, new XsrUiInput { Focusable = true }); tree.SetComponent(entity, input);
            return entity;
        }
    }
}
