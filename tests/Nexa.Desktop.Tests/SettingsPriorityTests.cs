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
        var selector = ShowSettingsFixtureControl(fixture, settings, ref scene,
            "SettingsSelector.game.process-priority", new(900, 650)).Entity;
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiSegmentedTrack>(selector) is not null);
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiScrollGesture>(selector) is not null);
        var choice = ShowSettingsFixtureControl(fixture, settings, ref scene,
            "SettingsOption.game.process-priority.below-normal", new(900, 650));
        AssertEqual("较低", choice.Text);
        Emit(fixture.Intents, "ui.settings.choice", choice.Entity);
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            scene = fixture.Shell.Render(new(900, 650));
            return !settings.SettingsWritePending
                && fixture.Foundation.Host.SettingsPolicy.Read(new(instance)).Value!.Values.Single(value => value.Key == "game.process-priority").Value.Value == "below-normal"
                && HasKey(fixture.Shell, scene, "SettingsOption.game.process-priority.below-normal")
                && FindByKey(fixture.Shell, scene, "SettingsOption.game.process-priority.below-normal").IsSelected;
        }, TimeSpan.FromSeconds(5)));
        AssertEqual("normal", fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(value => value.Key == "game.process-priority").Value.Value);
        Emit(fixture.Intents, "ui.settings.inherit", ShowSettingsFixtureControl(fixture, settings, ref scene,
            "SettingsInherit.game.process-priority", new(900, 650)).Entity);
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            scene = fixture.Shell.Render(new(900, 650));
            return !settings.SettingsWritePending
                && fixture.Foundation.Host.SettingsPolicy.Read(new(instance)).Value!.Values.Single(value => value.Key == "game.process-priority").Source == SettingsLayer.Builtin
                && HasKey(fixture.Shell, scene, "SettingsOption.game.process-priority.normal")
                && FindByKey(fixture.Shell, scene, "SettingsOption.game.process-priority.normal").IsSelected;
        }, TimeSpan.FromSeconds(5)));
    }
}
