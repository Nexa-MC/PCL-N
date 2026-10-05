using Nexa.Desktop.Ui;
using Nexa.Services.Composition;
using Nexa.Services.Files;
using Nexa.Services.Setup;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void StorageSettingsPreviewsCancelsAndQueuesBeforeClosing()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        string source = Path.Combine(fixture.TemporaryDirectory, "data"), target = Path.Combine(fixture.TemporaryDirectory, "target");
        string locator = Path.Combine(fixture.TemporaryDirectory, "bootstrap", "storage.json");
        Directory.CreateDirectory(Path.Combine(source, "settings")); File.WriteAllText(Path.Combine(source, "settings", "settings.json"), "preserve");
        LauncherStorageLocation.Save(locator, source);
        using var service = new StoragePreferencesService(new(source), locator, isIdle: () => true, tasks: fixture.Foundation.Host.Tasks);
        var runtime = FoundationRuntimeComposer.ComposeWithStorage(fixture.Foundation.Host, storagePreferences: service);
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, runtime.Queries, runtime.Commands, fixture.Store, fixture.Feedback);
        bool closed = false;
        settings.ConfigureStoragePreferences(_ => Task.FromResult<string?>(target), () => closed = true);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.storage").Entity);
        XsrUiEntityId Get(string key)
        {
            XsrUiEntityId found = default;
            fixture.Shell.Tree.Walk(settings.Page, entity => { if (fixture.Shell.Tree.Name(entity) == key) found = entity; return true; });
            AssertTrue(found.IsAssigned); return found;
        }
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return fixture.Shell.Tree.GetComponent<XsrUiInput>(Get("SettingsStorageMove"))!.Enabled; }, TimeSpan.FromSeconds(5)));
        void Preview()
        {
            Emit(fixture.Intents, "ui.settings.storage.move", Get("SettingsStorageMove"));
            AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Dialog is not null; }, TimeSpan.FromSeconds(5)));
        }
        Preview();
        var dialog = fixture.Feedback.Snapshot().Dialog!;
        AssertTrue(dialog.Message.Contains(source, StringComparison.Ordinal)); AssertTrue(dialog.Message.Contains(target, StringComparison.Ordinal));
        AssertFalse(service.Read().MigrationPending); AssertFalse(closed); AssertFalse(Directory.Exists(target));
        fixture.Feedback.ResolveDialog(dialog.Id, false); fixture.Shell.Render(new(1000, 650));
        AssertFalse(service.Read().MigrationPending); AssertFalse(closed);
        Preview(); dialog = fixture.Feedback.Snapshot().Dialog!;
        fixture.Feedback.ResolveDialog(dialog.Id, true);
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return closed; }, TimeSpan.FromSeconds(5)));
        AssertTrue(service.Read().MigrationPending); AssertEqual(source, LauncherStorageLocation.Read(locator)); AssertFalse(Directory.Exists(target));
    }

    private static void StorageSettingsNavigationRetiresLateConfirmation()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        string source = Path.Combine(fixture.TemporaryDirectory, "data"), target = Path.Combine(fixture.TemporaryDirectory, "target"), locator = Path.Combine(fixture.TemporaryDirectory, "storage.json");
        Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "record"), "keep");
        using var service = new StoragePreferencesService(new(source), locator, isIdle: () => true);
        var runtime = FoundationRuntimeComposer.ComposeWithStorage(fixture.Foundation.Host, storagePreferences: service);
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, runtime.Queries, runtime.Commands, fixture.Store, fixture.Feedback);
        bool closed = false; settings.ConfigureStoragePreferences(_ => Task.FromResult<string?>(target), () => closed = true);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.storage").Entity);
        fixture.Shell.Render(new(1000, 650));
        XsrUiEntityId move = default;
        fixture.Shell.Tree.Walk(settings.Page, entity => { if (fixture.Shell.Tree.Name(entity) == "SettingsStorageMove") move = entity; return true; });
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            scene = fixture.Shell.Render(new(1000, 650));
            fixture.Shell.Tree.Walk(settings.Page, entity => { if (fixture.Shell.Tree.Name(entity) == "SettingsStorageMove") move = entity; return true; });
            return move.IsAssigned && fixture.Shell.Tree.GetComponent<XsrUiInput>(move)!.Enabled;
        }, TimeSpan.FromSeconds(5)));
        Emit(fixture.Intents, "ui.settings.storage.move", move);
        AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Dialog is not null; }, TimeSpan.FromSeconds(5)));
        var retained = fixture.Feedback.Snapshot().Dialog!;
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.general").Entity); fixture.Shell.Render(new(1000, 650));
        AssertTrue(fixture.Feedback.Snapshot().Dialog is null);
        retained.Resolve(true); fixture.Shell.Render(new(1000, 650));
        AssertFalse(service.Read().MigrationPending); AssertFalse(closed); AssertFalse(Directory.Exists(target));
    }

    private static void StorageCleanupRequiresPreviewConfirmationAndPreservesRecoveryCards()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        string root = Path.Combine(fixture.TemporaryDirectory, "data"), locator = Path.Combine(fixture.TemporaryDirectory, "storage.json");
        Directory.CreateDirectory(Path.Combine(root, "settings"));
        string temporary = Path.Combine(root, "settings", ".settings.json." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temporary, "old"); File.SetLastWriteTimeUtc(temporary, DateTime.UtcNow - TimeSpan.FromDays(2));
        using var finished = fixture.Foundation.Host.Tasks.Begin(new("finished", "成功", ["run"])); finished.Complete();
        using var paused = fixture.Foundation.Host.Tasks.Begin(new("paused", "暂停", ["run"])); paused.Paused();
        using var service = new StoragePreferencesService(new(root), locator, isIdle: () => true, tasks: fixture.Foundation.Host.Tasks);
        var runtime = FoundationRuntimeComposer.ComposeWithStorage(fixture.Foundation.Host, storagePreferences: service);
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, runtime.Queries, runtime.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.storage").Entity); fixture.Shell.Render(new(1000, 650));
        XsrUiEntityId Get(string key)
        {
            XsrUiEntityId result = default; fixture.Shell.Tree.Walk(settings.Page, entity => { if (fixture.Shell.Tree.Name(entity) == key) result = entity; return true; });
            AssertTrue(result.IsAssigned); return result;
        }
        Emit(fixture.Intents, "ui.settings.storage.cleanup-temp", Get("SettingsStorageCleanTemporary"));
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Dialog is not null; }, TimeSpan.FromSeconds(5)));
        AssertTrue(File.Exists(temporary)); var dialog = fixture.Feedback.Snapshot().Dialog!;
        fixture.Feedback.ResolveDialog(dialog.Id, true);
        Guid temporaryCleanupNotice = default;
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            fixture.Shell.Render(new(1000, 650));
            var notice = fixture.Feedback.Snapshot().Notifications.FirstOrDefault(note => note.Message == "符合条件的项目已清理。");
            if (File.Exists(temporary) || notice is null) return false;
            temporaryCleanupNotice = notice.Id; return true;
        }, TimeSpan.FromSeconds(5)));
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return fixture.Shell.Tree.GetComponent<XsrUiInput>(Get("SettingsStorageCleanTasks"))!.Enabled; }, TimeSpan.FromSeconds(5)));
        Emit(fixture.Intents, "ui.settings.storage.cleanup-tasks", Get("SettingsStorageCleanTasks"));
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Dialog is not null; }, TimeSpan.FromSeconds(5)));
        dialog = fixture.Feedback.Snapshot().Dialog!; fixture.Feedback.ResolveDialog(dialog.Id, true);
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            fixture.Shell.Render(new(1000, 650));
            return fixture.Foundation.Host.Tasks.ReadEntries().Count == 1
                && fixture.Feedback.Snapshot().Notifications.Any(note => note.Message == "符合条件的项目已清理。" && note.Id != temporaryCleanupNotice);
        }, TimeSpan.FromSeconds(5)));
        AssertEqual("paused", fixture.Foundation.Host.Tasks.ReadEntries().Single().TaskId);
    }
}
