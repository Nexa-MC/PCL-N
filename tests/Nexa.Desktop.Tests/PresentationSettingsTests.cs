using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;

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
        AssertTrue(policy.Set(new("appearance.animation-fps", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "30"))).IsSuccess);
        List<bool> resizeCalls = [];
        List<int> frameRates = [];
        using var presentation = new DesktopPresentationSession(fixture.Shell, fixture.Store, resizeCalls.Add, frameRates.Add);
        AssertTrue(fixture.Shell.Renderer.ReducedMotion);
        AssertEqual(false, resizeCalls.Single());
        AssertEqual(30, frameRates.Single());
        fixture.Shell.Renderer.OptionalMotionSuspended = true;
        Set("appearance.animations-disabled", false);
        Set("appearance.lock-window", false);
        fixture.Shell.Render(new(1000, 650));
        AssertFalse(fixture.Shell.Renderer.ReducedMotion);
        AssertTrue(fixture.Shell.Renderer.OptionalMotionSuspended);
        AssertEqual(true, resizeCalls.Last());
        AssertTrue(policy.Set(new("appearance.animation-fps", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "120"))).IsSuccess);
        fixture.Shell.Render(new(1000, 650));
        AssertEqual(120, frameRates.Last());
        AssertEqual(2, frameRates.Count);
        int calls = resizeCalls.Count;
        for (int i = 0; i < 10; i++) fixture.Shell.Render(new(1000, 650));
        AssertEqual(calls, resizeCalls.Count);
        AssertEqual(2, frameRates.Count);

        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.appearance").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        var animations = FindByKey(fixture.Shell, scene, "SettingsOption.appearance.animations-disabled.true");
        AssertEqual(XsrUiSemanticRole.RadioButton, animations.Role);
        AssertEqual("关闭", animations.Text);
        Emit(fixture.Intents, "ui.settings.choice", animations.Entity);
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return fixture.Shell.Renderer.ReducedMotion; }, TimeSpan.FromSeconds(5)));
        presentation.Dispose();
        Set("appearance.lock-window", true);
        fixture.Shell.Render(new(1000, 650));
        AssertEqual(calls, resizeCalls.Count);
    }
}
