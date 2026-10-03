using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void VersionSettingsUseScopedControlsAndInsetForms()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        string instance = Path.GetFullPath("controls-ui-instance");
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => instance);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(900, 1500));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        scene = fixture.Shell.Render(new(900, 1500));
        var toggle = FindByKey(fixture.Shell, scene, "SettingsOption.game.auto-repair.false");
        AssertEqual(XsrUiSemanticRole.RadioButton, toggle.Role); AssertEqual("关闭", toggle.Text);
        AssertEqual(XsrUiSemanticRole.RadioGroup, FindByKey(fixture.Shell, scene, "SettingsSelector.game.auto-repair").Role);
        AssertTrue(toggle.IsFocusable && toggle.IsClickable);
        AssertEqual(XsrUiSemanticRole.RadioGroup, FindByKey(fixture.Shell, scene, "SettingsSelector.game.process-priority").Role);
        AssertEqual(XsrUiSemanticRole.RadioButton, FindByKey(fixture.Shell, scene, "SettingsOption.game.process-priority.high").Role);
        AssertEqual(XsrUiSemanticRole.RadioButton, FindByKey(fixture.Shell, scene, "SettingsAuto.game.memory").Role);
        AssertTrue(scene.Nodes.Any(n => n.Text == "Java 与内存"));
        AssertTrue(scene.Nodes.Any(n => n.Text == "高级参数"));
        AssertFalse(scene.Nodes.Any(n => fixture.Shell.Tree.Name(n.Entity).StartsWith("SettingsOption.", StringComparison.Ordinal) && n.Role == XsrUiSemanticRole.Button));
        var rows = scene.Nodes.Where(n => fixture.Shell.Tree.Name(n.Entity).StartsWith("SettingsRow.", StringComparison.Ordinal)).ToArray();
        AssertTrue(rows.Length > 5);
        foreach (var row in rows)
        {
            var surface = fixture.Shell.Tree.Parent(fixture.Shell.Tree.Parent(row.Entity));
            var card = scene.Nodes.Single(n => n.Entity == surface);
            AssertTrue(row.Rect.X >= card.Rect.X + 16 && row.Rect.X + row.Rect.Width <= card.Rect.X + card.Rect.Width - 16 + .1);
        }
        AssertTrue(fixture.Shell.Renderer.Focus(toggle.Entity));
        AssertTrue(fixture.Shell.Renderer.HandleKey(XsrUiKey.Space));
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            scene = fixture.Shell.Render(new(900, 1500));
            return FindByKey(fixture.Shell, scene, "SettingsOption.game.auto-repair.false").IsSelected == true;
        }, TimeSpan.FromSeconds(5)));
        AssertEqual("false", fixture.Foundation.Host.SettingsPolicy.Read(new(instance)).Value!.Values.Single(v => v.Key == "game.auto-repair").Value.Value);
        AssertEqual("true", fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(v => v.Key == "game.auto-repair").Value.Value);
        AssertEqual(toggle.Entity, fixture.Shell.Renderer.Focused);
        Emit(fixture.Intents, "ui.settings.inherit", FindByKey(fixture.Shell, scene, "SettingsInherit.game.auto-repair").Entity);
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            scene = fixture.Shell.Render(new(900, 1500));
            return FindByKey(fixture.Shell, scene, "SettingsOption.game.auto-repair.true").IsSelected == true;
        }, TimeSpan.FromSeconds(5)));
        // Keep the width narrow while including all newly migrated window rows.
        scene = fixture.Shell.Render(new(700, 1500));
        var binary = FindByKey(fixture.Shell, scene, "SettingsSelector.game.auto-repair");
        AssertTrue(binary.Rect.X + binary.Rect.Width <= 700);
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiSegmentedTrack>(binary.Entity) is not null);
    }
}
