using Nexa.Services.Files;
using Nexa.Services.Setup;
using Nexa.Xsr;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask StorageMigrationRejectsRootBoundGameRecordsAndLeavesExternalStoresAlone()
    {
        string temp = Path.Combine(Path.GetTempPath(), "nexa-storage-game-boundary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            string[] boundDirectories = [".nexa-java-jobs", ".nexa-install-jobs", ".nexa-modify", ".nexa-pack-jobs", ".nexa-rename", ".nexa-content-trash", "versions/example/Nexa/Recovery", ".task/loader"];
            for (int index = 0; index < boundDirectories.Length; index++)
            {
                string root = Path.Combine(temp, "data-" + index), target = Path.Combine(temp, "target-" + index), locator = Path.Combine(temp, "locator-" + index + ".json");
                string receipt = Path.Combine(root, "games", boundDirectories[index], "record.json");
                Directory.CreateDirectory(Path.GetDirectoryName(receipt)!); Directory.CreateDirectory(target);
                string originalReceipt = "{\"root\":\"" + root.Replace('\\', '/') + "\",\"state\":\"complete\"}";
                await File.WriteAllTextAsync(receipt, originalReceipt);
                await File.WriteAllTextAsync(Path.Combine(root, "user-world"), "preserve-world");
                LauncherStorageLocation.Save(locator, root); string originalLocator = await File.ReadAllTextAsync(locator);
                using var service = new StoragePreferencesService(new(root), locator, isIdle: () => true);
                var preview = await service.PreviewMigrationAsync(new(target));
                AssertFalse(preview.IsSuccess); AssertEqual(XsrErrorKind.Rejected, preview.Error!.Kind); AssertTrue(preview.Error.Message.Contains("Minecraft/Java", StringComparison.Ordinal));
                AssertEqual(originalReceipt, await File.ReadAllTextAsync(receipt)); AssertEqual(0, Directory.GetFileSystemEntries(target).Length);

                // An independently persisted request cannot bypass the same admission check at startup.
                string transaction = Guid.NewGuid().ToString("N");
                string pending = new System.Text.Json.Nodes.JsonObject
                {
                    ["version"] = 1,
                    ["transaction"] = transaction,
                    ["source"] = root,
                    ["destination"] = target,
                    ["revision"] = new string('A', 64)
                }.ToJsonString();
                await File.WriteAllTextAsync(locator + ".migration.json", pending);
                var startup = await StoragePreferencesService.CompletePendingMigrationAsync(root, locator);
                AssertFalse(startup.IsSuccess); AssertEqual(XsrErrorKind.Rejected, startup.Error!.Kind); AssertTrue(startup.Error.Message.Contains("Minecraft/Java", StringComparison.Ordinal));
                AssertEqual(originalReceipt, await File.ReadAllTextAsync(receipt)); AssertEqual("preserve-world", await File.ReadAllTextAsync(Path.Combine(root, "user-world")));
                AssertEqual(originalLocator, await File.ReadAllTextAsync(locator)); AssertEqual(pending, await File.ReadAllTextAsync(locator + ".migration.json"));
                AssertEqual(0, Directory.GetFileSystemEntries(target).Length);
                AssertTrue((await service.CancelQueuedMigrationAsync(new(transaction))).IsSuccess);
                AssertEqual(originalReceipt, await File.ReadAllTextAsync(receipt)); AssertEqual(originalLocator, await File.ReadAllTextAsync(locator));
            }

            string externalGame = Path.Combine(temp, "external-game"), externalReceipt = Path.Combine(externalGame, ".nexa-java-jobs", "completed", "intent.json");
            Directory.CreateDirectory(Path.GetDirectoryName(externalReceipt)!); await File.WriteAllTextAsync(externalReceipt, "external-completed-java-receipt");
            string data = Path.Combine(temp, "launcher-data"), moved = Path.Combine(temp, "launcher-moved"), bootstrap = Path.Combine(temp, "external-locator.json");
            Directory.CreateDirectory(Path.Combine(data, "profiles")); await File.WriteAllTextAsync(Path.Combine(data, "profiles", "explicit-path"), externalGame);
            Directory.CreateDirectory(Path.Combine(data, ".task", "scratch")); await File.WriteAllTextAsync(Path.Combine(data, ".task", "scratch", "ordinary-file"), "ordinary-scratch");
            LauncherStorageLocation.Save(bootstrap, data);
            using var movable = new StoragePreferencesService(new(data), bootstrap, isIdle: () => true);
            var allowed = await movable.PreviewMigrationAsync(new(moved)); AssertTrue(allowed.IsSuccess);
            AssertTrue((await movable.QueueMigrationAsync(new(moved, allowed.Value.Revision))).IsSuccess);
            AssertTrue((await StoragePreferencesService.CompletePendingMigrationAsync(data, bootstrap)).IsSuccess);
            AssertEqual(moved, LauncherStorageLocation.Read(bootstrap)); AssertEqual(externalGame, await File.ReadAllTextAsync(Path.Combine(moved, "profiles", "explicit-path")));
            AssertEqual("external-completed-java-receipt", await File.ReadAllTextAsync(externalReceipt)); AssertEqual(externalGame, await File.ReadAllTextAsync(Path.Combine(data, "profiles", "explicit-path")));
            AssertEqual("ordinary-scratch", await File.ReadAllTextAsync(Path.Combine(moved, ".task", "scratch", "ordinary-file")));
        }
        finally { Directory.Delete(temp, true); }
    }

    private static async ValueTask StorageMalformedRecordsAndCleanupKindsRejectWithoutMutation()
    {
        string temp = Path.Combine(Path.GetTempPath(), "nexa-storage-malformed-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            string root = Path.Combine(temp, "root"), locator = Path.Combine(temp, "storage.json");
            Directory.CreateDirectory(root); await File.WriteAllTextAsync(Path.Combine(root, "user-record"), "keep-original");
            LauncherStorageLocation.Save(locator, root);
            using var service = new StoragePreferencesService(new(root), locator, isIdle: () => true);
            var unknownKind = (StorageCleanupKind)int.MaxValue;
            var unknownPreview = await service.PreviewCleanupAsync(new(unknownKind));
            AssertFalse(unknownPreview.IsSuccess); AssertEqual(XsrErrorKind.Rejected, unknownPreview.Error!.Kind);
            var unknownApply = await service.CleanupAsync(new(unknownKind, "unused"));
            AssertFalse(unknownApply.IsSuccess); AssertEqual(XsrErrorKind.Rejected, unknownApply.Error!.Kind);

            foreach (string malformed in new[] { "[]", "{\"version\":999,\"transaction\":\"unsupported\"}", new string(' ', 16_385) })
            {
                await File.WriteAllTextAsync(locator + ".migration.json", malformed);
                var startup = await StoragePreferencesService.CompletePendingMigrationAsync(root, locator);
                AssertFalse(startup.IsSuccess); AssertEqual(XsrErrorKind.Rejected, startup.Error!.Kind);
                var status = service.ReadStatus(); AssertFalse(status.IsSuccess); AssertEqual(XsrErrorKind.Rejected, status.Error!.Kind);
                var cancel = await service.CancelQueuedMigrationAsync(new("invalid-preview"));
                AssertFalse(cancel.IsSuccess); AssertEqual(XsrErrorKind.Rejected, cancel.Error!.Kind);
                AssertEqual(malformed, await File.ReadAllTextAsync(locator + ".migration.json"));
                AssertEqual(root, LauncherStorageLocation.Read(locator));
                AssertEqual("keep-original", await File.ReadAllTextAsync(Path.Combine(root, "user-record")));
            }
            File.Delete(locator + ".migration.json");
            string transaction = Guid.NewGuid().ToString("N"), stage = Path.Combine(root, ".nexa-storage-cleanup-" + transaction);
            Directory.CreateDirectory(stage); await File.WriteAllTextAsync(Path.Combine(stage, ".nexa-storage-owner"), transaction);
            await File.WriteAllTextAsync(Path.Combine(stage, "retained-recovery-bytes"), "keep-staged");
            string malformedCleanup = new System.Text.Json.Nodes.JsonObject
            {
                ["version"] = 1,
                ["transaction"] = transaction,
                ["source"] = root,
                ["phase"] = "unknown",
                ["files"] = new System.Text.Json.Nodes.JsonArray()
            }.ToJsonString();
            await File.WriteAllTextAsync(locator + ".cleanup.json", malformedCleanup);
            var recovery = await StoragePreferencesService.CompletePendingMigrationAsync(root, locator);
            AssertFalse(recovery.IsSuccess); AssertEqual(XsrErrorKind.Rejected, recovery.Error!.Kind);
            var cleanup = await service.CleanupAsync(new(StorageCleanupKind.TemporaryFiles, "unused"));
            AssertFalse(cleanup.IsSuccess); AssertEqual(XsrErrorKind.Rejected, cleanup.Error!.Kind);
            AssertEqual("keep-staged", await File.ReadAllTextAsync(Path.Combine(stage, "retained-recovery-bytes")));
            AssertEqual(malformedCleanup, await File.ReadAllTextAsync(locator + ".cleanup.json"));
            AssertEqual(root, LauncherStorageLocation.Read(locator)); AssertEqual("keep-original", await File.ReadAllTextAsync(Path.Combine(root, "user-record")));
        }
        finally { Directory.Delete(temp, true); }
    }

    private static async ValueTask StorageInterruptedStagesRetryAndPublishedCopiesCanCancelSafely()
    {
        string temp = Path.Combine(Path.GetTempPath(), "nexa-storage-interrupted-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            string source = Path.Combine(temp, "source"), target = Path.Combine(temp, "target"), locator = Path.Combine(temp, "storage.json");
            Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "record"), "before"); LauncherStorageLocation.Save(locator, source);
            using var service = new StoragePreferencesService(new(source), locator, isIdle: () => true);
            var preview = await service.PreviewMigrationAsync(new(target));
            AssertFalse((await service.QueueMigrationAsync(new(Path.Combine(temp, "different-target"), preview.Value.Revision))).IsSuccess);
            AssertTrue((await service.QueueMigrationAsync(new(target, preview.Value.Revision))).IsSuccess);
            string transaction = service.Read().PendingTransaction!;
            string stage = Path.Combine(temp, ".nexa-storage-migrate-" + transaction); Directory.CreateDirectory(stage);
            // Simulate interruption between creating the stage and persisting its ownership marker.
            await File.WriteAllTextAsync(Path.Combine(stage, ".nexa-storage-owner"), "partial");
            AssertTrue((await StoragePreferencesService.CompletePendingMigrationAsync(source, locator)).IsSuccess);
            AssertEqual("before", await File.ReadAllTextAsync(Path.Combine(target, "record"))); AssertFalse(Directory.Exists(stage));

            source = Path.Combine(temp, "stale-source"); target = Path.Combine(temp, "stale-target");
            Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "record"), "old"); LauncherStorageLocation.Save(locator, source);
            using var staleService = new StoragePreferencesService(new(source), locator, isIdle: () => true);
            preview = await staleService.PreviewMigrationAsync(new(target)); AssertTrue((await staleService.QueueMigrationAsync(new(target, preview.Value.Revision))).IsSuccess);
            transaction = staleService.Read().PendingTransaction!;
            Directory.CreateDirectory(target); await File.WriteAllTextAsync(Path.Combine(target, ".nexa-storage-owner"), transaction);
            await File.WriteAllTextAsync(Path.Combine(target, "record"), "verified-old-copy");
            // An owned published copy plus changed source must not strand an un-cancelable request.
            await File.WriteAllTextAsync(Path.Combine(source, "record"), "new-current-data");
            AssertFalse((await StoragePreferencesService.CompletePendingMigrationAsync(source, locator)).IsSuccess);
            AssertTrue((await staleService.CancelQueuedMigrationAsync(new(transaction))).IsSuccess);
            AssertEqual(source, LauncherStorageLocation.Read(locator)); AssertEqual("new-current-data", await File.ReadAllTextAsync(Path.Combine(source, "record")));
            AssertEqual("verified-old-copy", await File.ReadAllTextAsync(Path.Combine(target, "record"))); AssertFalse(staleService.Read().MigrationPending);
        }
        finally { Directory.Delete(temp, true); }
    }

    private static async ValueTask StorageCleanupRollsBackRenamesWhenIdleGuardChanges()
    {
        string temp = Path.Combine(Path.GetTempPath(), "nexa-storage-clean-rollback-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            string root = Path.Combine(temp, "root"), locator = Path.Combine(temp, "storage.json"); Directory.CreateDirectory(Path.Combine(root, "settings"));
            string[] candidates = Enumerable.Range(0, 2).Select(_ => Path.Combine(root, "settings", ".settings.json." + Guid.NewGuid().ToString("N") + ".tmp")).ToArray();
            foreach (string file in candidates) { await File.WriteAllTextAsync(file, "rollback"); File.SetLastWriteTimeUtc(file, DateTime.UtcNow - TimeSpan.FromDays(2)); }
            int checks = 0;
            using var service = new StoragePreferencesService(new(root), locator, isIdle: () => ++checks < 5);
            var preview = await service.PreviewCleanupAsync(new(StorageCleanupKind.TemporaryFiles)); AssertEqual(2, preview.Value.Entries.Count);
            AssertFalse((await service.CleanupAsync(new(StorageCleanupKind.TemporaryFiles, preview.Value.Revision))).IsSuccess);
            foreach (string file in candidates) AssertEqual("rollback", await File.ReadAllTextAsync(file));
            AssertFalse(File.Exists(locator + ".cleanup.json")); AssertEqual(0, Directory.EnumerateDirectories(root, ".nexa-storage-cleanup-*").Count());
        }
        finally { Directory.Delete(temp, true); }
    }
}
