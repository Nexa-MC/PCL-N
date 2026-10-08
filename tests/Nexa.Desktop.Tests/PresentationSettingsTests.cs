using Nexa.Desktop.Ui;
using Nexa.Services.Accounts;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Settings;
using Nexa.Services.Tasks;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void LowPowerPresentationYieldsToWorkAndRestoresPreference()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([])); fixture.Controller.Dispose();
        var policy = fixture.Foundation.Host.SettingsPolicy;
        void Set(string key, string value) => AssertTrue(policy.Set(new(key, SettingsLayer.Global, new(SettingsOverrideMode.Custom, value))).IsSuccess);
        Set("appearance.animation-fps", "120"); Set("appearance.low-power", "true");
        List<int> rates = [];
        using var session = new DesktopPresentationSession(fixture.Shell, fixture.Store, _ => { }, rates.Add);
        AssertEqual(120, rates.Last());
        void Pump() => fixture.Shell.Render(new(1000, 650));
        fixture.Shell.PublishWindowActivity(false, false); Pump(); AssertEqual(10, rates.Last());
        using var task = fixture.Foundation.Host.Tasks.Begin(new("low-power-task", "Fixture", []));
        Pump(); AssertEqual(120, rates.Last()); task.Complete(); Pump(); AssertEqual(10, rates.Last());
        var launch = fixture.Store.Resolve(MinecraftLaunchProgressState.SnapshotKey);
        fixture.Store.Publish(launch, new MinecraftLaunchProgressSnapshot(true, "prepare", 0, "", "", false, null));
        Pump(); AssertEqual(120, rates.Last());
        fixture.Store.Publish(launch, MinecraftLaunchProgressSnapshot.Empty); Pump(); AssertEqual(10, rates.Last());
        var login = fixture.Store.Resolve(AccountOnboardingState.Login);
        fixture.Store.Publish(login, new AccountLoginSnapshot(1, AccountLoginPhase.Starting, "")); Pump(); AssertEqual(120, rates.Last());
        fixture.Store.Publish(login, new AccountLoginSnapshot(1, AccountLoginPhase.Completed, "")); Pump(); AssertEqual(10, rates.Last());
        Set("appearance.animation-fps", "30"); Pump(); AssertEqual(10, rates.Last());
        fixture.Shell.PublishWindowActivity(true, false); Pump(); AssertEqual(30, rates.Last());
        fixture.Shell.PublishWindowActivity(true, true); Pump(); AssertEqual(10, rates.Last());
        Set("appearance.low-power", "false"); Pump(); AssertEqual(30, rates.Last());
        AssertFalse(fixture.Shell.Renderer.ReducedMotion);
        int calls = rates.Count; for (int i = 0; i < 10; i++) Pump(); AssertEqual(calls, rates.Count);
        session.Dispose(); Set("appearance.animation-fps", "60"); Pump(); AssertEqual(calls, rates.Count);
        AssertEqual("60", policy.Read(new()).Value!.Values.Single(v => v.Key == "appearance.animation-fps").Value.Value);
    }

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
        const string animationsKey = "SettingsOption.appearance.animations-disabled.true";
        XsrUiEntityId animationOption = default;
        fixture.Shell.Tree.Walk(settings.Page, entity =>
        { if (fixture.Shell.Tree.Name(entity) == animationsKey) animationOption = entity; return true; });
        AssertTrue(animationOption.IsAssigned);
        // Appearance settings preceding Animation now exceed this real window's viewport.
        var sections = FindByKey(fixture.Shell, scene, "SettingsSections");
        var scroll = fixture.Shell.Tree.GetComponent<XsrUiScroll>(sections.Entity)!;
        double maximum = sections.Scroll!.Value.MaximumOffsetY;
        for (double offset = 0; offset <= maximum + 100; offset += 100)
        {
            scroll.OffsetY = Math.Min(offset, maximum);
            fixture.Shell.Tree.MarkDirty(sections.Entity, XsrUiDirtyKinds.Layout);
            scene = fixture.Shell.Render(new(1000, 650));
            if (scene.Nodes.Any(node => node.Entity == animationOption && node.Rect.Y >= sections.Rect.Y
                && node.Rect.Y + node.Rect.Height <= sections.Rect.Y + sections.Rect.Height)) break;
        }
        var animations = FindByKey(fixture.Shell, scene, animationsKey);
        AssertEqual(animationOption, animations.Entity);
        AssertTrue(animations.Rect.Y >= sections.Rect.Y && animations.Rect.Y + animations.Rect.Height <= sections.Rect.Y + sections.Rect.Height);
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
