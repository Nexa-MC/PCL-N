using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void RetiredLowPowerFactsCannotOverridePresentationPreferences()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexa-retired-presentation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, new JsonObject
            {
                ["schemaVersion"] = 1,
                ["booleanOptions"] = new JsonObject { ["UiUltraLowPowerMode"] = true, ["SystemDisableUiAnimations"] = false },
                ["integerOptions"] = new JsonObject { ["UiAniFPS"] = 119 },
                ["textOptions"] = new JsonObject
                {
                    [SettingsPolicySchema.StorageKey] = """{"version":1,"global":{"appearance.low-power":{"mode":"Custom","value":"true"},"appearance.reduced-motion":{"mode":"Custom","value":"true"}},"instances":{}}""",
                },
            }.ToJsonString());
            string original = File.ReadAllText(path);
            var schema = LauncherDefaults.CreateSchema();
            var builder = new XsrStateStoreBuilder();
            SettingsService.DeclareState(builder, schema);
            SettingsPolicyContract.DeclareState(builder);
            XsrUiShellWindowState.Declare(builder);
            // A foreign host retaining the old cell must not revive the removed consumer.
            var retiredKey = XsrSemanticId.Parse("UiUltraLowPowerMode");
            builder.Cell<bool>(retiredKey, "Fixture.RetiredLowPower");
            var state = builder.Build();
            var settings = new SettingsService(state, schema, new LauncherSettingsJsonPort(path, schema));
            var policy = new SettingsPolicyService(settings);
            AssertTrue(settings.LoadError is null);
            AssertFalse(policy.Read(new()).Value!.Values.Any(value => value.Key == "appearance.low-power"));
            state.Publish(state.Resolve(retiredKey), true);
            var queries = new XsrQueryRouterBuilder();
            queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
                (query, _) => ValueTask.FromResult(policy.Read(query)));
            var shell = new XsrUiShell(state);
            shell.PublishWindowActivity(false, false);
            ConcurrentQueue<Action> posted = new();
            List<int> rates = [];
            using var session = new DesktopPresentationSession(shell, state, _ => { }, rates.Add,
                queries.Build(new NoopDispatchObserver()), posted.Enqueue);
            void Pump()
            {
                while (posted.TryDequeue(out var action)) action();
                shell.Render(new(1000, 650));
            }
            void WaitForMotion(bool expected) => AssertTrue(SpinWait.SpinUntil(() =>
            { Pump(); return shell.Renderer.ReducedMotion == expected; }, TimeSpan.FromSeconds(5)));
            void Set(string key, string value) => AssertTrue(policy.Set(new(key, SettingsLayer.Global,
                new(SettingsOverrideMode.Custom, value))).IsSuccess);
            WaitForMotion(true);
            AssertEqual(120, rates.Single());
            AssertEqual(original, File.ReadAllText(path));
            shell.PublishWindowActivity(true, true); Pump(); AssertEqual(120, rates.Last());
            Set("appearance.animation-fps", "30"); Pump(); AssertEqual(30, rates.Last());
            shell.PublishWindowActivity(false, false); Pump(); AssertEqual(30, rates.Last());
            Set("appearance.reduced-motion", "false"); WaitForMotion(false);
            Set("appearance.animations-disabled", "true"); Pump(); AssertTrue(shell.Renderer.ReducedMotion);
            Set("appearance.animation-fps", "144"); Pump(); AssertEqual(144, rates.Last());
            state.Publish(state.Resolve(retiredKey), false); Pump(); AssertEqual(144, rates.Last());
            Set("appearance.animations-disabled", "false"); Pump(); AssertFalse(shell.Renderer.ReducedMotion);
            AssertTrue(JsonNode.Parse(File.ReadAllText(path))!["booleanOptions"]!["UiUltraLowPowerMode"]!.GetValue<bool>());
            int calls = rates.Count; for (int i = 0; i < 10; i++) Pump(); AssertEqual(calls, rates.Count);
            session.Dispose(); Set("appearance.animation-fps", "60"); Pump(); AssertEqual(calls, rates.Count);
            AssertEqual("60", policy.Read(new()).Value!.Values.Single(value => value.Key == "appearance.animation-fps").Value.Value);
        }
        finally { Directory.Delete(directory, true); }
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
