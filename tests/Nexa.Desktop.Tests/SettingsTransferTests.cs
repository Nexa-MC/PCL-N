using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private const string SettingsImportDocument = "{\"version\":1,\"scope\":\"global\",\"values\":{\"game.width\":{\"mode\":\"Custom\",\"value\":\"1024\"}}}";

    private static void SettingsTransferUsesInstanceScopeAndDiscardsChangedIdentity()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        string first = Path.GetFullPath("instance-scoped-a"), second = Path.GetFullPath("instance-scoped-b");
        string? current = first;
        var policy = fixture.Foundation.Host.SettingsPolicy;
        AssertTrue(policy.Set(new("game.width", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "2048"))).IsSuccess);
        AssertTrue(policy.Set(new("game.width", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "1234"), first)).IsSuccess);
        AssertTrue(policy.Set(new("game.width", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "1500"), second)).IsSuccess);
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => current);
        string? exported = null;
        settings.ConfigureSettingsTransfer(_ => Task.FromResult<string?>(SettingsImportDocument.Replace("global", "instance", StringComparison.Ordinal)),
            (document, _) => { exported = document; return Task.FromResult(true); });
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        var sections = FindByKey(fixture.Shell, scene, "SettingsSections").Entity;
        fixture.Shell.Tree.GetComponent<XsrUiScroll>(sections)!.OffsetY = 10000;
        fixture.Shell.Tree.MarkDirty(sections, XsrUiDirtyKinds.Layout); scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.data.export", FindByKey(fixture.Shell, scene, "SettingsExport").Entity);
        AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Notifications.Any(note => note.Message == "设置已导出。"); }, TimeSpan.FromSeconds(5)));
        AssertTrue(exported is not null);
        using var document = System.Text.Json.JsonDocument.Parse(exported!);
        AssertEqual("instance", document.RootElement.GetProperty("scope").GetString());
        AssertEqual("1234", document.RootElement.GetProperty("values").GetProperty("game.width").GetProperty("value").GetString());
        AssertFalse(exported!.Contains("instance-scoped-a", StringComparison.Ordinal));
        Emit(fixture.Intents, "ui.settings.data.import", FindByKey(fixture.Shell, scene, "SettingsImport").Entity);
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Dialog is not null; }, TimeSpan.FromSeconds(5)));
        var dialog = fixture.Feedback.Snapshot().Dialog!;
        current = null; fixture.Shell.Render(new(1000, 650));
        AssertTrue(fixture.Feedback.Snapshot().Dialog is null);
        current = first; fixture.Shell.Render(new(1000, 650));
        dialog.Resolve(true); fixture.Shell.Render(new(1000, 650));
        current = second; fixture.Shell.Render(new(1000, 650));
        AssertEqual("1234", policy.Read(new(first)).Value!.Values.Single(value => value.Key == "game.width").Value.Value);
        AssertEqual("1500", policy.Read(new(second)).Value!.Values.Single(value => value.Key == "game.width").Value.Value);
        AssertEqual("2048", policy.Read(new()).Value!.Values.Single(value => value.Key == "game.width").Value.Value);
    }

    private static void SettingsTransferPreviewsCancelsAppliesAndFiltersExport()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        string? exported = null;
        settings.ConfigureSettingsTransfer(_ => Task.FromResult<string?>(SettingsImportDocument), (document, _) =>
        { exported = document; return Task.FromResult(true); });
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.storage").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        void Import()
        {
            Emit(fixture.Intents, "ui.settings.data.import", FindByKey(fixture.Shell, scene, "SettingsImport").Entity);
            AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Dialog is not null; }, TimeSpan.FromSeconds(5)));
        }
        int original = fixture.Foundation.Host.Settings.GetValue<int>("LaunchArgumentWindowWidth").Value;
        Import();
        AssertEqual(original, fixture.Foundation.Host.Settings.GetValue<int>("LaunchArgumentWindowWidth").Value);
        var dialog = fixture.Feedback.Snapshot().Dialog!;
        AssertTrue(dialog.Message.Contains("默认宽度", StringComparison.Ordinal));
        AssertFalse(dialog.Message.Contains("game.width", StringComparison.Ordinal));
        AssertTrue(fixture.Feedback.ResolveDialog(dialog.Id, false)); scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual(original, fixture.Foundation.Host.Settings.GetValue<int>("LaunchArgumentWindowWidth").Value);
        Import(); dialog = fixture.Feedback.Snapshot().Dialog!;
        AssertTrue(fixture.Feedback.ResolveDialog(dialog.Id, true));
        AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return fixture.Foundation.Host.Settings.GetValue<int>("LaunchArgumentWindowWidth").Value == 1024; }, TimeSpan.FromSeconds(5)));
        // Drain the completed import before issuing another operation.
        AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return FindByKey(fixture.Shell, scene, "SettingsExport").IsEnabled; }, TimeSpan.FromSeconds(5)));
        Emit(fixture.Intents, "ui.settings.data.export", FindByKey(fixture.Shell, scene, "SettingsExport").Entity);
        AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Notifications.Any(note => note.Message == "设置已导出。"); }, TimeSpan.FromSeconds(5)));
        AssertTrue(exported is not null);
        foreach (string secret in new[] { "network.proxy-password", "network.proxy-user", "java.runtime", "game.jvm", "game.wrapper" })
            AssertFalse(exported!.Contains(secret, StringComparison.Ordinal));
        AssertTrue(exported!.Contains("1024", StringComparison.Ordinal));
    }

    private static void SettingsImportRejectsStaleRevisionAndLateConfirmation()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        settings.ConfigureSettingsTransfer(_ => Task.FromResult<string?>(SettingsImportDocument), (_, _) => Task.FromResult(true));
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.storage").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        void Preview()
        {
            Emit(fixture.Intents, "ui.settings.data.import", FindByKey(fixture.Shell, scene, "SettingsImport").Entity);
            AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Dialog is not null; }, TimeSpan.FromSeconds(5)));
        }
        int original = fixture.Foundation.Host.Settings.GetValue<int>("LaunchArgumentWindowWidth").Value;
        Preview(); var dialog = fixture.Feedback.Snapshot().Dialog!;
        AssertTrue(fixture.Foundation.Host.SettingsPolicy.Set(new("game.height", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "720"))).IsSuccess);
        AssertTrue(fixture.Feedback.ResolveDialog(dialog.Id, true));
        AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Notifications.Any(note => note.Message.StartsWith("设置未导入", StringComparison.Ordinal)); }, TimeSpan.FromSeconds(5)));
        AssertEqual(original, fixture.Foundation.Host.Settings.GetValue<int>("LaunchArgumentWindowWidth").Value);
        Preview(); dialog = fixture.Feedback.Snapshot().Dialog!;
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.general").Entity);
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(fixture.Feedback.Snapshot().Dialog is null);
        dialog.Resolve(true); // A retained callback cannot authorize a later page/scope.
        fixture.Shell.Render(new(1000, 650));
        AssertEqual(original, fixture.Foundation.Host.Settings.GetValue<int>("LaunchArgumentWindowWidth").Value);
    }

    private static void SettingsImportDiscardsLateReadAndRejectsWrongScope()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        var document = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var returned = new ManualResetEventSlim();
        settings.ConfigureSettingsTransfer(async _ => { string? text = await document.Task; returned.Set(); return text; }, (_, _) => Task.FromResult(true));
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.storage").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.data.import", FindByKey(fixture.Shell, scene, "SettingsImport").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.general").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        document.SetResult(SettingsImportDocument); AssertTrue(returned.Wait(TimeSpan.FromSeconds(5)));
        scene = fixture.Shell.Render(new(1000, 650)); AssertTrue(fixture.Feedback.Snapshot().Dialog is null);
        settings.ConfigureSettingsTransfer(_ => Task.FromResult<string?>(SettingsImportDocument.Replace("global", "instance", StringComparison.Ordinal)), (_, _) => Task.FromResult(true));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.storage").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.data.import", FindByKey(fixture.Shell, scene, "SettingsImport").Entity);
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Notifications.Any(note => note.Message.Contains("作用域", StringComparison.Ordinal)); }, TimeSpan.FromSeconds(5)));
        AssertTrue(fixture.Feedback.Snapshot().Dialog is null);
    }
}
