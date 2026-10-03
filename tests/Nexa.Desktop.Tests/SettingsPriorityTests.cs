using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void SettingsPrioritySelectorUsesReadableChoicesAndInstanceScope()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        string instance = Path.GetFullPath("priority-ui-instance");
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => instance);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(900, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        scene = fixture.Shell.Render(new(900, 650));
        var selector = FindByKey(fixture.Shell, scene, "SettingsSelector.game.process-priority").Entity;
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiSegmentedTrack>(selector) is not null);
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiScrollGesture>(selector) is not null);
        var choice = FindByKey(fixture.Shell, scene, "SettingsOption.game.process-priority.below-normal");
        AssertEqual("较低", choice.Text);
        Emit(fixture.Intents, "ui.settings.choice", choice.Entity);
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            scene = fixture.Shell.Render(new(900, 650));
            return fixture.Foundation.Host.SettingsPolicy.Read(new(instance)).Value!.Values.Single(value => value.Key == "game.process-priority").Value.Value == "below-normal";
        }, TimeSpan.FromSeconds(5)));
        AssertEqual("normal", fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(value => value.Key == "game.process-priority").Value.Value);
        Emit(fixture.Intents, "ui.settings.inherit", FindByKey(fixture.Shell, scene, "SettingsInherit.game.process-priority").Entity);
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            scene = fixture.Shell.Render(new(900, 650));
            return fixture.Foundation.Host.SettingsPolicy.Read(new(instance)).Value!.Values.Single(value => value.Key == "game.process-priority").Source == SettingsLayer.Builtin;
        }, TimeSpan.FromSeconds(5)));
    }
}
