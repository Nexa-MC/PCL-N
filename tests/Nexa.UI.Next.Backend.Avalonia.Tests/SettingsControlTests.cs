using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static async Task VerifyNativeSettingsControls(XsrUiShell shell, AvaloniaUiSceneSurface surface)
    {
        var page = shell.Tree.Create("native-controls");
        shell.Tree.SetComponent(page, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        XsrUiEntityId Control(XsrUiEntityId parent, string name, XsrUiSemanticRole role)
        {
            var entity = shell.Tree.Create(name); shell.Tree.Attach(entity, parent);
            shell.Tree.SetComponent(entity, new XsrUiElement { Width = 100, Height = 32 });
            shell.Tree.SetComponent(entity, new XsrUiSemantic(role, name));
            shell.Tree.SetComponent(entity, new XsrUiInput { Clickable = true, Focusable = true });
            shell.Tree.SetComponent(entity, new XsrUiVisualStyle { Foreground = new(11, 91, 203) });
            return entity;
        }
        var toggle = Control(page, "native-switch", XsrUiSemanticRole.Switch);
        var state = new XsrUiToggle(true); shell.Tree.SetComponent(toggle, state);
        var checkbox = Control(page, "native-checkbox", XsrUiSemanticRole.CheckBox);
        shell.Tree.SetComponent(checkbox, new XsrUiToggle()); shell.Tree.SetComponent(checkbox, new XsrUiText("Include saves"));
        var group = shell.Tree.Create("native-radio-group"); shell.Tree.Attach(group, page);
        shell.Tree.SetComponent(group, new XsrUiSemantic(XsrUiSemanticRole.RadioGroup, "Mode"));
        shell.Tree.SetComponent(group, new XsrUiStackPanel(XsrUiOrientation.Horizontal));
        var radio = Control(group, "native-radio", XsrUiSemanticRole.RadioButton);
        shell.Tree.SetComponent(radio, new XsrUiSelection { IsSelected = true });
        shell.Stage.Navigation.Replace(page); shell.Renderer.ReducedMotion = false; surface.CommitScene();
        AvaloniaUiSceneNodeControl Native(XsrUiEntityId entity) => surface.Children.OfType<AvaloniaUiSceneNodeControl>().Single(c => c.Node.Entity == entity);
        var native = Native(toggle);
        AssertEqual(AutomationControlType.CheckBox, AutomationProperties.GetControlTypeOverride(native));
        AssertEqual(AutomationControlType.RadioButton, AutomationProperties.GetControlTypeOverride(Native(radio)));
        var selection = AssertNotNull(ControlAutomationPeer.CreatePeerForElement(Native(group)).GetProvider<ISelectionProvider>());
        var item = AssertNotNull(ControlAutomationPeer.CreatePeerForElement(Native(radio)).GetProvider<ISelectionItemProvider>());
        AssertTrue(ReferenceEquals(selection, item.SelectionContainer)); AssertEqual(1, selection.GetSelection().Count);
        state.IsChecked = false; shell.Tree.MarkDirty(toggle, XsrUiDirtyKinds.Paint); surface.CommitScene();
        AssertEqual(1d, native.PresentedToggleProgress); // State changes start from the current presentation.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (native.PresentedToggleProgress > .001 && DateTime.UtcNow < deadline) await Task.Delay(16);
        AssertTrue(native.PresentedToggleProgress < .001);
        state.IsChecked = true; shell.Tree.MarkDirty(toggle, XsrUiDirtyKinds.Paint); surface.CommitScene();
        AssertTrue(native.PresentedToggleProgress < .001);
        shell.Renderer.ReducedMotion = true;
        deadline = DateTime.UtcNow.AddSeconds(2);
        while (native.PresentedToggleProgress < .999 && DateTime.UtcNow < deadline) await Task.Delay(16);
        AssertEqual(1d, native.PresentedToggleProgress);
        Console.WriteLine("PASS: native settings controls paint, connect radio groups and settle reduced-motion toggles");
    }

    private static void SettingsTogglePeersUseConfirmedStateAndDisabledInput()
    {
        var tree = new XsrUiTree();
        var root = tree.Create("settings");
        var toggle = tree.Create("switch"); tree.Attach(toggle, root);
        tree.SetComponent(toggle, new XsrUiElement { Width = 52, Height = 32 });
        tree.SetComponent(toggle, new XsrUiSemantic(XsrUiSemanticRole.Switch, "Repair files"));
        tree.SetComponent(toggle, new XsrUiToggle(true));
        var input = new XsrUiInput { Focusable = true, Clickable = true }; tree.SetComponent(toggle, input);
        tree.SetComponent(toggle, new XsrUiCommandBinding(XsrSemanticId.Parse("settings.toggle")));
        var intents = new XsrUiIntentBuffer();
        var renderer = new XsrUiRenderer(tree, new XsrStateStoreBuilder().Build(), intents); renderer.SetRoot(root);
        var original = Node(renderer.Render(), toggle);
        var control = new AvaloniaUiSceneNodeControl(entity => renderer.Focus(entity), entity => renderer.Activate(entity), () => true);
        control.Apply(original);
        var peer = ControlAutomationPeer.CreatePeerForElement(control);
        var provider = AssertNotNull(peer.GetProvider<IToggleProvider>());
        AssertTrue(peer.GetProvider<IInvokeProvider>() is null);
        AssertEqual(ToggleState.On, provider.ToggleState);
        AssertEqual(1d, control.PresentedToggleProgress);
        provider.Toggle();
        AssertEqual(1, intents.Count);
        AssertEqual(ToggleState.On, provider.ToggleState); // Intent does not fabricate a committed setting.
        AssertTrue(renderer.Focus(toggle)); AssertTrue(renderer.HandleKey(XsrUiKey.Space));
        AssertEqual(2, intents.Count);
        tree.GetComponent<XsrUiToggle>(toggle)!.IsChecked = false;
        control.Apply(Node(renderer.Render(), toggle));
        AssertEqual(ToggleState.Off, provider.ToggleState);
        AssertEqual(0d, control.PresentedToggleProgress);
        AssertEqual(true, original.IsChecked); // Prior scene remains immutable.
        input.Enabled = false; control.Apply(Node(renderer.Render(), toggle));
        provider.Toggle(); AssertEqual(2, intents.Count);
        AssertFalse(renderer.HandleKey(XsrUiKey.Space));

        var check = new AvaloniaUiSceneNodeControl(_ => { }, _ => { }, () => true);
        check.Apply(original with { Role = XsrUiSemanticRole.CheckBox, IsChecked = false });
        var checkProvider = AssertNotNull(ControlAutomationPeer.CreatePeerForElement(check).GetProvider<IToggleProvider>());
        AssertEqual(ToggleState.Off, checkProvider.ToggleState);
        check.Apply(original with { Role = XsrUiSemanticRole.CheckBox });
        AssertEqual(ToggleState.On, checkProvider.ToggleState);
    }

    private static void RadioPeersExposeExclusiveGroupSelection()
    {
        var tree = new XsrUiTree(); int requests = 0;
        var group = new AvaloniaUiSceneNodeControl(_ => { }, _ => { }, () => true);
        group.Apply(Node(tree.Create("group"), XsrUiSemanticRole.RadioGroup));
        var item = new AvaloniaUiSceneNodeControl(_ => { }, _ => requests++, () => true);
        item.Apply(Node(tree.Create("option"), XsrUiSemanticRole.RadioButton, selected: true, focusable: true, clickable: true));
        item.SetSelectionContainer(group); group.AddSelectionItem(item);
        var selection = AssertNotNull(ControlAutomationPeer.CreatePeerForElement(group).GetProvider<ISelectionProvider>());
        var option = AssertNotNull(ControlAutomationPeer.CreatePeerForElement(item).GetProvider<ISelectionItemProvider>());
        AssertTrue(option.IsSelected); AssertFalse(selection.CanSelectMultiple);
        AssertEqual(1, selection.GetSelection().Count);
        AssertTrue(ReferenceEquals(selection, option.SelectionContainer));
        option.Select(); AssertEqual(1, requests);
        option.RemoveFromSelection(); AssertTrue(option.IsSelected);
        AssertTrue(selection.IsSelectionRequired);
        group.Apply(group.Node with { IsSelectionRequired = false });
        item.Apply(item.Node with { IsSelected = false });
        AssertFalse(selection.IsSelectionRequired); AssertEqual(0, selection.GetSelection().Count);
        item.Apply(item.Node with { IsEnabled = false, IsClickable = false });
        option.Select(); AssertEqual(1, requests);
    }
}
