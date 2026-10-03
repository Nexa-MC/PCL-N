using Nexa.Desktop.Ui;
using Nexa.Services.Logging;
using Nexa.Services.Settings;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void LogPreferencesUseSettingsControlsAndApplyImmediately()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 900));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.privacy").Entity);
        scene = fixture.Shell.Render(new(1000, 900));
        var selector = FindByKey(fixture.Shell, scene, "SettingsSelector.diagnostics.log-level").Entity;
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiSegmentedTrack>(selector) is not null);
        Emit(fixture.Intents, "ui.settings.choice", FindByKey(fixture.Shell, scene, "SettingsOption.diagnostics.log-level.1").Entity);
        fixture.Shell.Render(new(1000, 900));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.Logging.MaximumLevel == LogLevel.Warn, TimeSpan.FromSeconds(5)));
        scene = fixture.Shell.Render(new(1000, 900));
        var input = FindByKey(fixture.Shell, scene, "SettingsInput.diagnostics.log-lines").Entity;
        fixture.Shell.Renderer.Focus(input); fixture.Shell.Renderer.SetTextInputValue(input, "60");
        Emit(fixture.Intents, "ui.settings.edit", FindByKey(fixture.Shell, scene, "SettingsEdit.diagnostics.log-lines").Entity);
        fixture.Shell.Render(new(1000, 900));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.Logging.RetentionLimit == 60, TimeSpan.FromSeconds(5)));
        AssertEqual(input, fixture.Shell.Renderer.Focused);
        AssertEqual("60", fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(item => item.Key == "diagnostics.log-lines").Value.Value);
    }
}
