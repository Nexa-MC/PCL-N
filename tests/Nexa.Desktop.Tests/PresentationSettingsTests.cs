using Nexa.Desktop.Ui;
using Nexa.Services.Settings;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void PresentationSettingsApplyWithoutNavigation()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        fixture.Controller.Dispose(); // Keep the activity policy under this test's explicit control.
        var policy = fixture.Foundation.Host.SettingsPolicy;
        void Set(string key, bool value) => AssertTrue(policy.Set(new(key, SettingsLayer.Global,
            new(SettingsOverrideMode.Custom, value ? "true" : "false"))).IsSuccess);
        Set("appearance.animations-disabled", true);
        Set("appearance.lock-window", true);
        List<bool> resizeCalls = [];
        using var presentation = new DesktopPresentationSession(fixture.Shell, fixture.Store, resizeCalls.Add);
        AssertTrue(fixture.Shell.Renderer.ReducedMotion);
        AssertEqual(false, resizeCalls.Single());
        fixture.Shell.Renderer.OptionalMotionSuspended = true;
        Set("appearance.animations-disabled", false);
        Set("appearance.lock-window", false);
        fixture.Shell.Render(new(1000, 650));
        AssertFalse(fixture.Shell.Renderer.ReducedMotion);
        AssertTrue(fixture.Shell.Renderer.OptionalMotionSuspended);
        AssertEqual(true, resizeCalls.Last());
        int calls = resizeCalls.Count;
        for (int i = 0; i < 10; i++) fixture.Shell.Render(new(1000, 650));
        AssertEqual(calls, resizeCalls.Count);

        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.appearance").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        var disable = FindByKey(fixture.Shell, scene, "SettingsOption.appearance.animations-disabled.true");
        var enable = FindByKey(fixture.Shell, scene, "SettingsOption.appearance.animations-disabled.false");
        AssertEqual("关闭", disable.Text);
        AssertEqual("开启", enable.Text);
        Emit(fixture.Intents, "ui.settings.choice", disable.Entity);
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return fixture.Shell.Renderer.ReducedMotion; }, TimeSpan.FromSeconds(5)));
        presentation.Dispose();
        Set("appearance.lock-window", true);
        fixture.Shell.Render(new(1000, 650));
        AssertEqual(calls, resizeCalls.Count);
    }
}
