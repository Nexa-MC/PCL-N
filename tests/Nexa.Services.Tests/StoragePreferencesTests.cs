using System.Text.Json.Nodes;
using Nexa.Services.Files;
using Nexa.Services.Setup;
using Nexa.Services.Tasks;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask StorageMigrationQueuesAndCopiesBeforeStartupConsumers()
    {
        string temp = Path.Combine(Path.GetTempPath(), "nexa-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string source = Path.Combine(temp, "source"), target = Path.Combine(temp, "target"), locator = Path.Combine(temp, "bootstrap", "storage.json");
            Directory.CreateDirectory(Path.Combine(source, "settings")); Directory.CreateDirectory(Path.Combine(source, "profiles"));
            Directory.CreateDirectory(Path.Combine(source, "logs")); Directory.CreateDirectory(Path.Combine(source, "cache", "empty"));
            await File.WriteAllTextAsync(Path.Combine(source, "settings", "settings.json"), "settings-byte-contract");
            await File.WriteAllTextAsync(Path.Combine(source, "profiles", "profiles.json"), "explicit-external-minecraft-path");
            await File.WriteAllTextAsync(Path.Combine(source, "logs", "launcher.log"), "before");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(Path.Combine(source, "profiles", "profiles.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            LauncherStorageLocation.Save(locator, source);
            using var service = new StoragePreferencesService(new(source), locator, isIdle: () => true);
            var preview = await service.PreviewMigrationAsync(new(target)); AssertTrue(preview.IsSuccess);
            AssertEqual(3, preview.Value.Files); AssertFalse(Directory.Exists(target)); AssertFalse(service.Read().MigrationPending);
            AssertTrue((await service.QueueMigrationAsync(new(target, preview.Value.Revision))).IsSuccess);
            AssertEqual(source, LauncherStorageLocation.Read(locator)); AssertFalse(Directory.Exists(target));
            AssertTrue(service.Read().MigrationPending);
            // Shutdown can flush more log bytes; protected settings/profile revision remains bound.
            await File.AppendAllTextAsync(Path.Combine(source, "logs", "launcher.log"), "-shutdown");
            AssertTrue((await StoragePreferencesService.CompletePendingMigrationAsync(source, locator)).IsSuccess);
            AssertEqual(target, LauncherStorageLocation.Read(locator));
            foreach (string relative in new[] { "settings/settings.json", "profiles/profiles.json", "logs/launcher.log" })
                AssertEqual(await File.ReadAllTextAsync(Path.Combine(source, relative)), await File.ReadAllTextAsync(Path.Combine(target, relative)));
            AssertTrue(Directory.Exists(Path.Combine(target, "cache", "empty"))); AssertFalse(File.Exists(locator + ".migration.json"));
            if (!OperatingSystem.IsWindows())
            {
                AssertEqual(File.GetUnixFileMode(source), File.GetUnixFileMode(target));
                AssertEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(target, "profiles", "profiles.json")));
            }
            AssertTrue((await StoragePreferencesService.CompletePendingMigrationAsync(target, locator)).IsSuccess);
        }
        finally { Directory.Delete(temp, true); }
    }

    private static async ValueTask StorageMigrationRejectsStaleOccupiedLockedAndCancelledRequests()
    {
        string temp = Path.Combine(Path.GetTempPath(), "nexa-storage-reject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string source = Path.Combine(temp, "source"), target = Path.Combine(temp, "target"), locator = Path.Combine(temp, "storage.json");
            Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "record"), "before"); LauncherStorageLocation.Save(locator, source);
            using var service = new StoragePreferencesService(new(source), locator, isIdle: () => true);
            var preview = await service.PreviewMigrationAsync(new(target)); AssertTrue(preview.IsSuccess);
            await File.WriteAllTextAsync(Path.Combine(source, "record"), "after");
            AssertFalse((await service.QueueMigrationAsync(new(target, preview.Value.Revision))).IsSuccess);
            AssertFalse(service.Read().MigrationPending);
            foreach (string invalid in new[] { source, Path.Combine(source, "nested"), Path.GetPathRoot(source)! })
                AssertFalse((await service.PreviewMigrationAsync(new(invalid))).IsSuccess);
            Directory.CreateDirectory(target); await File.WriteAllTextAsync(Path.Combine(target, "world"), "preserve");
            AssertFalse((await service.PreviewMigrationAsync(new(target))).IsSuccess); AssertEqual("preserve", await File.ReadAllTextAsync(Path.Combine(target, "world")));
            using var locked = new StoragePreferencesService(new(source), locator, true, () => true);
            AssertFalse((await locked.PreviewMigrationAsync(new(Path.Combine(temp, "locked")))).IsSuccess);
            string link = Path.Combine(temp, "linked"); Directory.CreateSymbolicLink(link, source);
            AssertFalse((await service.PreviewMigrationAsync(new(Path.Combine(link, "target")))).IsSuccess);
            Directory.Delete(link);
            target = Path.Combine(temp, "cancel"); preview = await service.PreviewMigrationAsync(new(target));
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { AssertFalse((await service.QueueMigrationAsync(new(target, preview.Value.Revision), stop.Token)).IsSuccess); }
            catch (OperationCanceledException) { }
            AssertFalse(File.Exists(locator + ".migration.json")); AssertEqual(source, LauncherStorageLocation.Read(locator));
            using var busy = new StoragePreferencesService(new(source), locator, isIdle: () => false);
            AssertFalse((await busy.QueueMigrationAsync(new(target, preview.Value.Revision))).IsSuccess);
        }
        finally { Directory.Delete(temp, true); }
    }

    private static async ValueTask StorageMigrationFailedLocatorCommitPreservesSourceAndRetries()
    {
        string temp = Path.Combine(Path.GetTempPath(), "nexa-storage-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string source = Path.Combine(temp, "source"), target = Path.Combine(temp, "target"), locator = Path.Combine(temp, "storage.json");
            Directory.CreateDirectory(source); Directory.CreateDirectory(target); await File.WriteAllTextAsync(Path.Combine(source, "record"), "original");
            using var service = new StoragePreferencesService(new(source), locator, isIdle: () => true);
            var preview = await service.PreviewMigrationAsync(new(target)); AssertTrue((await service.QueueMigrationAsync(new(target, preview.Value.Revision))).IsSuccess);
            // A directory at the fixed file location causes publication to fail only after copying.
            Directory.CreateDirectory(locator);
            AssertFalse((await StoragePreferencesService.CompletePendingMigrationAsync(source, locator)).IsSuccess);
            AssertEqual("original", await File.ReadAllTextAsync(Path.Combine(source, "record")));
            AssertTrue(Directory.Exists(target)); AssertEqual(0, Directory.EnumerateFileSystemEntries(target).Count());
            AssertTrue(File.Exists(locator + ".migration.json"));
            Directory.Delete(locator);
            AssertTrue((await StoragePreferencesService.CompletePendingMigrationAsync(source, locator)).IsSuccess);
            AssertEqual(target, LauncherStorageLocation.Read(locator));
        }
        finally { Directory.Delete(temp, true); }
    }

    private static async ValueTask StorageCleanupOnlyRemovesEligibleTemporariesAndSuccessfulCards()
    {
        string temp = Path.Combine(Path.GetTempPath(), "nexa-storage-clean-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string root = Path.Combine(temp, "root"), locator = Path.Combine(temp, "storage.json"); Directory.CreateDirectory(Path.Combine(root, "settings"));
            Directory.CreateDirectory(Path.Combine(root, "cache")); Directory.CreateDirectory(Path.Combine(root, ".task"));
            string old = Path.Combine(root, "settings", ".settings.json." + Guid.NewGuid().ToString("N") + ".tmp");
            string recent = Path.Combine(root, "cache", "record.tmp-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(old, "old-atomic-write"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromDays(2));
            foreach (string path in new[] { recent, Path.Combine(root, "settings", "settings.json.invalid"), Path.Combine(root, "cache", "asset.jar"), Path.Combine(root, ".task", "checkpoint.tmp-" + Guid.NewGuid().ToString("N")) })
                await File.WriteAllTextAsync(path, "keep");
            var center = NewTaskCenter(out _);
            using var finished = center.Begin(new("done", "成功", ["run"])); finished.Complete();
            using var failed = center.Begin(new("failed", "失败", ["run"])); failed.Fail("error");
            using var paused = center.Begin(new("paused", "暂停", ["run"])); paused.Paused();
            using var canceled = center.Begin(new("canceled", "取消", ["run"])); canceled.Canceled();
            using var service = new StoragePreferencesService(new(root), locator, isIdle: () => true, tasks: center);
            var preview = await service.PreviewCleanupAsync(new(StorageCleanupKind.TemporaryFiles)); AssertTrue(preview.IsSuccess); AssertEqual(1, preview.Value.Entries.Count);
            await File.WriteAllTextAsync(old, "changed"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromDays(2));
            AssertFalse((await service.CleanupAsync(new(StorageCleanupKind.TemporaryFiles, preview.Value.Revision))).IsSuccess); AssertTrue(File.Exists(old));
            preview = await service.PreviewCleanupAsync(new(StorageCleanupKind.TemporaryFiles));
            AssertTrue((await service.CleanupAsync(new(StorageCleanupKind.TemporaryFiles, preview.Value.Revision))).IsSuccess); AssertFalse(File.Exists(old)); AssertTrue(File.Exists(recent));
            var taskPreview = await service.PreviewCleanupAsync(new(StorageCleanupKind.FinishedTasks)); AssertEqual(1, taskPreview.Value.Entries.Count);
            using var added = center.Begin(new("new", "新成功任务", ["run"])); added.Complete();
            AssertFalse((await service.CleanupAsync(new(StorageCleanupKind.FinishedTasks, taskPreview.Value.Revision))).IsSuccess);
            taskPreview = await service.PreviewCleanupAsync(new(StorageCleanupKind.FinishedTasks));
            AssertTrue((await service.CleanupAsync(new(StorageCleanupKind.FinishedTasks, taskPreview.Value.Revision))).IsSuccess);
            AssertEqual(3, center.ReadEntries().Count); AssertTrue(center.ReadEntries().All(entry => entry.State != TaskCenterEntryState.Finished));
            AssertTrue(File.Exists(Path.Combine(root, "settings", "settings.json.invalid"))); AssertTrue(File.Exists(Path.Combine(root, "cache", "asset.jar")));
        }
        finally { Directory.Delete(temp, true); }
    }

    private static async ValueTask StorageCleanupStartupRestoresInterruptedRenames()
    {
        string temp = Path.Combine(Path.GetTempPath(), "nexa-storage-recover-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            string root = Path.Combine(temp, "root"), locator = Path.Combine(temp, "storage.json"), transaction = Guid.NewGuid().ToString("N");
            string relative = "settings/.settings.json." + Guid.NewGuid().ToString("N") + ".tmp";
            string stage = Path.Combine(root, ".nexa-storage-cleanup-" + transaction);
            Directory.CreateDirectory(Path.Combine(stage, "settings"));
            await File.WriteAllTextAsync(Path.Combine(stage, ".nexa-storage-owner"), transaction);
            byte[] content = System.Text.Encoding.UTF8.GetBytes("rollback-original"); await File.WriteAllBytesAsync(Path.Combine(stage, relative), content);
            var record = new JsonObject
            {
                ["version"] = 1,
                ["transaction"] = transaction,
                ["source"] = root,
                ["phase"] = "prepared",
                ["files"] = new JsonArray(new JsonObject
                {
                    ["path"] = relative,
                    ["bytes"] = content.Length,
                    ["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content))
                })
            };
            await File.WriteAllTextAsync(locator + ".cleanup.json", record.ToJsonString());
            AssertTrue((await StoragePreferencesService.CompletePendingMigrationAsync(root, locator)).IsSuccess);
            AssertEqual("rollback-original", await File.ReadAllTextAsync(Path.Combine(root, relative)));
            AssertFalse(Directory.Exists(stage)); AssertFalse(File.Exists(locator + ".cleanup.json"));
        }
        finally { Directory.Delete(temp, true); }
    }
}
