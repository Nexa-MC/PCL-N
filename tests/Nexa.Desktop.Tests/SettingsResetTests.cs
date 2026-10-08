using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void SettingsResetRequiresConfirmationAndRetiresLateDecisions()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        var policy = fixture.Foundation.Host.SettingsPolicy;
        AssertTrue(policy.Set(new("game.width", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1024"))).IsSuccess);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        void OpenStorage()
        {
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.storage").Entity);
            ShowSettingsFixtureControl(fixture, settings, ref scene, "SettingsReset");
        }
        void Preview()
        {
            var reset = ShowSettingsFixtureControl(fixture, settings, ref scene, "SettingsReset");
            AssertTrue(reset.IsEnabled);
            Emit(fixture.Intents, "ui.settings.data.reset", reset.Entity);
            AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Dialog is not null; }, TimeSpan.FromSeconds(5)));
        }
        OpenStorage(); Preview(); var dialog = fixture.Feedback.Snapshot().Dialog!;
        AssertTrue(dialog.Message.Contains("默认宽度", StringComparison.Ordinal));
        AssertFalse(dialog.Message.Contains("game.width", StringComparison.Ordinal));
        AssertTrue(fixture.Feedback.ResolveDialog(dialog.Id, false)); scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual("1024", policy.Read(new()).Value!.Values.Single(value => value.Key == "game.width").Value.Value);
        Preview(); dialog = fixture.Feedback.Snapshot().Dialog!;
        AssertTrue(policy.Set(new("game.height", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "720"))).IsSuccess);
        AssertTrue(fixture.Feedback.ResolveDialog(dialog.Id, true));
        AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Notifications.Any(note => note.Message.StartsWith("设置未恢复", StringComparison.Ordinal)); }, TimeSpan.FromSeconds(5)));
        AssertEqual("1024", policy.Read(new()).Value!.Values.Single(value => value.Key == "game.width").Value.Value);
        Preview(); dialog = fixture.Feedback.Snapshot().Dialog!;
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.general").Entity);
        scene = fixture.Shell.Render(new(1000, 650)); dialog.Resolve(true);
        OpenStorage(); scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual("1024", policy.Read(new()).Value!.Values.Single(value => value.Key == "game.width").Value.Value);
        Preview(); dialog = fixture.Feedback.Snapshot().Dialog!;
        AssertTrue(fixture.Feedback.ResolveDialog(dialog.Id, true));
        AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Notifications.Any(note => note.Message == "设置已恢复。"); }, TimeSpan.FromSeconds(5)));
        AssertEqual("854", policy.Read(new()).Value!.Values.Single(value => value.Key == "game.width").Value.Value);
    }

    private static void SettingsInstanceResetRestoresInheritanceWithoutNativeFileCallbacks()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        string first = Path.GetFullPath("reset-ui-instance-a"), second = Path.GetFullPath("reset-ui-instance-b");
        string? current = first;
        var policy = fixture.Foundation.Host.SettingsPolicy;
        AssertTrue(policy.Set(new("game.width", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1024"))).IsSuccess);
        AssertTrue(policy.Set(new("game.width", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "1234"), first)).IsSuccess);
        AssertTrue(policy.Set(new("game.width", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "1500"), second)).IsSuccess);
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => current);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        void Preview()
        {
            // Scope changes and completed profile reads can replace the card and its controls.
            var reset = ShowSettingsFixtureControl(fixture, settings, ref scene, "SettingsReset");
            AssertTrue(reset.IsEnabled);
            AssertFalse(FindByKey(fixture.Shell, scene, "SettingsImport").IsEnabled);
            Emit(fixture.Intents, "ui.settings.data.reset", reset.Entity);
            AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Dialog is not null; }, TimeSpan.FromSeconds(5)));
        }
        Preview(); var dialog = fixture.Feedback.Snapshot().Dialog!;
        current = null; scene = fixture.Shell.Render(new(1000, 650));
        current = first; scene = fixture.Shell.Render(new(1000, 650)); dialog.Resolve(true);
        scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual("1234", policy.Read(new(first)).Value!.Values.Single(value => value.Key == "game.width").Value.Value);
        AssertEqual(SettingsLayer.Instance, policy.Read(new(first)).Value!.Values.Single(value => value.Key == "game.width").Source);
        AssertEqual("1024", policy.Read(new()).Value!.Values.Single(value => value.Key == "game.width").Value.Value);
        AssertEqual("1500", policy.Read(new(second)).Value!.Values.Single(value => value.Key == "game.width").Value.Value);
        // Restoring a cleared selection starts at overview; its retired reset confirmation cannot act.
        AssertEqual("overview", settings.SelectedSection);
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual("game", settings.SelectedSection);
        Preview(); dialog = fixture.Feedback.Snapshot().Dialog!;
        AssertTrue(fixture.Feedback.ResolveDialog(dialog.Id, true));
        AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Notifications.Any(note => note.Message == "设置已恢复。"); }, TimeSpan.FromSeconds(5)));
        var width = policy.Read(new(first)).Value!.Values.Single(value => value.Key == "game.width");
        AssertEqual(SettingsLayer.Global, width.Source); AssertEqual("1024", width.Value.Value);
        AssertEqual("1500", policy.Read(new(second)).Value!.Values.Single(value => value.Key == "game.width").Value.Value);
    }
}
