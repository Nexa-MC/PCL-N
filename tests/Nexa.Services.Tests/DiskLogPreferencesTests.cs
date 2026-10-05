using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Nexa.Services.Foundation;
using Nexa.Services.Logging;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static string DiskArchivePath(string root) => Path.Combine(root,
        "launcher-archive-20260101T000000000-" + Guid.NewGuid().ToString("N") + ".log");

    private static async ValueTask DiskLogsRotateWithinPhysicalBounds()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "launcher.log");
            var sink = new FileLogSink(path, 512);
            var entry = new LogEntry(1, DateTimeOffset.UtcNow, LogLevel.Info, "Launch", "", null);
            for (int index = 0; index < 350; index++)
                sink.Write(entry, $"[12:00:00.000] [Info] [Launch] record-{index:D4}-" + new string('x', 75));
            sink.Write(entry, "[12:00:00.000] [Info] [Launch] " + new string('界', 100_000));
            await sink.DisposeAsync();
            var owned = new OwnedLogFiles(path);
            var archives = owned.Archives();
            AssertEqual(OwnedLogFiles.MaximumArchiveCount, archives.Count);
            AssertTrue(archives.All(file => file.Length <= 512));
            AssertTrue(new FileInfo(path).Length <= 512);
            AssertTrue(File.ReadAllText(path).Contains("disk record truncated", StringComparison.Ordinal));
            AssertTrue(archives.Sum(file => file.Length) <= OwnedLogFiles.MaximumArchiveBytes);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask DiskRetentionProtectsCurrentUnrelatedFilesAndLinks()
    {
        string root = CreateTempDirectory(), outside = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "launcher.log"), ancient = DiskArchivePath(root), recent = DiskArchivePath(root);
            File.WriteAllText(path, "current must remain");
            File.WriteAllText(ancient, "old generated archive"); File.SetLastWriteTimeUtc(ancient, DateTime.UtcNow.AddDays(-30));
            File.WriteAllText(recent, "recent generated archive"); File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-2));
            string unrelated = Path.Combine(root, "personal.log"), text = Path.Combine(root, "notes.txt");
            string malformed = Path.Combine(root, "launcher-archive-not-an-owned-identity.log");
            File.WriteAllText(unrelated, "personal"); File.WriteAllText(text, "notes"); File.WriteAllText(malformed, "private");
            string nested = Path.Combine(root, "nested"); Directory.CreateDirectory(nested);
            string nestedArchive = DiskArchivePath(nested); File.WriteAllText(nestedArchive, "nested");
            File.SetLastWriteTimeUtc(nestedArchive, DateTime.UtcNow.AddDays(-30));
            string external = Path.Combine(outside, "private.log"); File.WriteAllText(external, "external-private-text");
            string? link = null;
            if (!OperatingSystem.IsWindows())
            { link = DiskArchivePath(root); File.CreateSymbolicLink(link, external); }
            var sink = new FileLogSink(path);
            try
            {
                // Until the persisted policy arrives, a constructor must not apply a guessed age.
                await sink.ExportAsync(Path.Combine(root, "before-policy.zip")); AssertTrue(File.Exists(ancient));
                sink.SetRetentionDays(7);
                await sink.ExportAsync(Path.Combine(root, "seven-days.zip"));
                AssertFalse(File.Exists(ancient)); AssertTrue(File.Exists(recent));
                sink.SetRetentionDays(1);
                await sink.ExportAsync(Path.Combine(root, "one-day.zip"));
                AssertFalse(File.Exists(recent));
                AssertEqual("current must remain", File.ReadAllText(path));
                foreach (string preserved in new[] { unrelated, text, malformed, nestedArchive, external }) AssertTrue(File.Exists(preserved));
                if (link is not null) AssertTrue(new FileInfo(link).LinkTarget is not null);
                AssertEqual("external-private-text", File.ReadAllText(external));
            }
            finally { await sink.DisposeAsync(); }
            if (!OperatingSystem.IsWindows())
            {
                string directoryLink = Path.Combine(root, "linked-logs"); Directory.CreateSymbolicLink(directoryLink, outside);
                var linkedSink = new FileLogSink(Path.Combine(directoryLink, "launcher.log"));
                linkedSink.SetRetentionDays(1);
                linkedSink.Write(new(1, DateTimeOffset.UtcNow, LogLevel.Info, "Launch", "", null), "never written");
                await linkedSink.DisposeAsync();
                AssertFalse(File.Exists(Path.Combine(outside, "launcher.log"))); AssertTrue(File.Exists(external));
            }
        }
        finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
    }

    private static async ValueTask DiskRetentionEnforcesArchiveCountAndByteCeilings()
    {
        string root = CreateTempDirectory();
        try
        {
            for (int index = 0; index < 3; index++)
            {
                string archive = DiskArchivePath(root);
                using (var file = File.Create(archive)) file.SetLength(32L * 1024 * 1024);
                File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddDays(-10 - index));
            }
            for (int index = 0; index < 40; index++)
            {
                string archive = DiskArchivePath(root); File.WriteAllText(archive, "x");
                File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddMinutes(-index));
            }
            var sink = new FileLogSink(Path.Combine(root, "launcher.log"));
            sink.SetRetentionDays(90); await sink.DisposeAsync();
            var archives = new OwnedLogFiles(Path.Combine(root, "launcher.log")).Archives();
            AssertTrue(archives.Count <= OwnedLogFiles.MaximumArchiveCount);
            AssertTrue(archives.Sum(file => file.Length) <= OwnedLogFiles.MaximumArchiveBytes);
            AssertFalse(File.Exists(Path.Combine(root, "launcher.log"))); // Policy does not manufacture an empty active file.
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask DiskLogPolicyAppliesCommittedChangesWithoutRendering()
    {
        string root = CreateTempDirectory();
        try
        {
            var port = new PolicyFailingPort(); var (_, initial) = PolicyFixture(port);
            AssertTrue(initial.Set(new("diagnostics.disk-log-days", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "7"))).IsSuccess);
            string path = Path.Combine(root, "launcher.log"), retained = DiskArchivePath(root);
            File.WriteAllText(retained, "[00:00:01.000] [Warn] [Launch] history");
            File.SetLastWriteTimeUtc(retained, DateTime.UtcNow.AddDays(-2));
            var sink = new FileLogSink(path);
            using var host = FoundationComposer.Compose(port, LauncherDefaults.CreateSchema(), new ThrowingProfilePort(), configureLogging: log => log.AddSink(sink));
            try
            {
                AssertEqual(7, host.Logging.DiskRetentionDays);
                await sink.ExportAsync(Path.Combine(root, "initial.zip")); AssertTrue(File.Exists(retained));
                port.Fail = true;
                AssertFalse(host.SettingsPolicy.Set(new("diagnostics.disk-log-days", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1"))).IsSuccess);
                AssertEqual(7, host.Logging.DiskRetentionDays);
                await sink.ExportAsync(Path.Combine(root, "failed-save.zip")); AssertTrue(File.Exists(retained));
                port.Fail = false;
                AssertTrue(host.SettingsPolicy.Set(new("diagnostics.disk-log-days", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1"))).IsSuccess);
                AssertEqual(1, host.Logging.DiskRetentionDays);
                await sink.ExportAsync(Path.Combine(root, "committed.zip")); AssertFalse(File.Exists(retained));
                AssertFalse(host.SettingsPolicy.Set(new("diagnostics.disk-log-days", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "7"), root)).IsSuccess);
                AssertFalse(host.SettingsPolicy.Set(new("diagnostics.disk-log-days", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "0"))).IsSuccess);
                AssertFalse(host.SettingsPolicy.Set(new("diagnostics.disk-log-days", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "91"))).IsSuccess);
                AssertTrue(host.SettingsPolicy.Set(new("diagnostics.disk-log-days", SettingsLayer.Global, new(SettingsOverrideMode.Inherit))).IsSuccess);
                AssertEqual(7, host.Logging.DiskRetentionDays);
                host.Dispose();
                AssertTrue(host.SettingsPolicy.Set(new("diagnostics.disk-log-days", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "30"))).IsSuccess);
                AssertEqual(7, host.Logging.DiskRetentionDays);
            }
            finally { await sink.DisposeAsync(); }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask DiskRetentionChangesDoNotWaitForLogPublication()
    {
        using var observer = new BlockingRetentionObserver();
        using var log = CreateLogService(128, observer);
        var writer = Task.Run(() => log.Info("Launch", "blocked publication"));
        AssertTrue(observer.Entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var preference = Task.Run(() => log.DiskRetentionDays = 30);
            AssertTrue(preference.Wait(TimeSpan.FromSeconds(2))); AssertEqual(30, log.DiskRetentionDays);
        }
        finally { observer.Release.Set(); await writer; }
    }

    private static async ValueTask DiskLogExportIncludesArchivedFactsAndExcludesPrivateText()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "launcher.log");
            var sink = new FileLogSink(path, 512);
            using var log = CreateLogService(4); log.AddSink(sink);
            var firstTime = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
            for (int index = 0; index < 12; index++)
            {
                var entry = new LogEntry(index + 1, firstTime.AddSeconds(index), LogLevel.Warn,
                    index == 6 ? "unlabeled-module-secret" : "Launch", "unlabeled-password Alice /home/alice/private", "private-stack");
                sink.Write(entry, entry.ToDisplayText());
                log.Info("Launch", "ring entry " + index);
            }
            string archive = DiskArchivePath(root);
            File.WriteAllText(archive, "[01:02:03.004] [Error] [Settings] old-disk-only-secret\n");
            File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddDays(-1));
            string unrelated = Path.Combine(root, "personal.log");
            File.WriteAllText(unrelated, "[02:03:04.005] [Error] [Settings] unrelated-private-text\n");
            string? link = null;
            if (!OperatingSystem.IsWindows())
            { link = DiskArchivePath(root); File.CreateSymbolicLink(link, unrelated); }
            string target = Path.Combine(root, "logs.zip");
            try
            {
                AssertEqual(4, log.GetSnapshot().Count);
                await sink.ExportAsync(target);
                AssertTrue(new OwnedLogFiles(path).Archives().Count > 0);
                using (var zip = ZipFile.OpenRead(target))
                {
                    AssertEqual(1, zip.Entries.Count); AssertEqual("logs.json", zip.Entries[0].FullName);
                    using var reader = new StreamReader(zip.Entries[0].Open()); string json = await reader.ReadToEndAsync();
                    foreach (string secret in new[] { "unlabeled-password", "Alice", "/home/", "private-stack", "old-disk-only-secret", "unlabeled-module-secret", "unrelated-private-text", "02:03:04.005", root })
                        AssertFalse(json.Contains(secret, StringComparison.Ordinal));
                    AssertTrue(json.Contains("01:02:03.004", StringComparison.Ordinal));
                    AssertTrue(json.Contains(firstTime.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), StringComparison.Ordinal));
                    AssertTrue(json.Contains("archive", StringComparison.Ordinal)); AssertTrue(json.Contains("unknown", StringComparison.Ordinal));
                    using var document = JsonDocument.Parse(json);
                    AssertTrue(document.RootElement.GetProperty("entries").GetArrayLength() >= 13);
                    AssertFalse(document.RootElement.GetProperty("records_truncated").GetBoolean());
                }
                byte[] before = await File.ReadAllBytesAsync(target);
                try { await sink.ExportAsync(target); throw new InvalidOperationException("Existing export overwritten."); }
                catch (IOException) { }
                byte[] after = await File.ReadAllBytesAsync(target);
                AssertTrue(before.SequenceEqual(after));
                AssertEqual(0, Directory.GetFiles(root, ".nexa-log-export-*.tmp").Length);
                using var stop = new CancellationTokenSource(); stop.Cancel();
                try { await sink.ExportAsync(Path.Combine(root, "cancelled.zip"), stop.Token); throw new InvalidOperationException("Cancellation ignored."); }
                catch (OperationCanceledException) { }
                AssertFalse(File.Exists(Path.Combine(root, "cancelled.zip")));
            }
            finally { await sink.DisposeAsync(); }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask DiskLogExportBoundsHistoryAndCleansCancelledDiskRead()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "launcher.log"), archive = DiskArchivePath(root);
            File.WriteAllText(archive, string.Concat(Enumerable.Repeat("[12:34:56.789] [Info] [Launch] private-text\n", 2500)));
            var sink = new FileLogSink(path);
            try
            {
                string bounded = Path.Combine(root, "bounded.zip"); await sink.ExportAsync(bounded);
                using (var zip = ZipFile.OpenRead(bounded))
                {
                    using var reader = new StreamReader(zip.GetEntry("logs.json")!.Open());
                    using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
                    AssertEqual(2000, json.RootElement.GetProperty("entries").GetArrayLength());
                    AssertTrue(json.RootElement.GetProperty("records_truncated").GetBoolean());
                }
                AssertTrue(new FileInfo(bounded).Length < 2 * 1024 * 1024);
                // Sparse input keeps this a cheap temp-tree test while exercising actual bounded disk IO.
                using (var file = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None)) file.SetLength(64L * 1024 * 1024);
                using var stop = new CancellationTokenSource();
                string cancelled = Path.Combine(root, "cancelled-read.zip");
                Task writing = sink.ExportAsync(cancelled, stop.Token);
                bool staging = SpinWait.SpinUntil(() => Directory.GetFiles(root, ".nexa-log-export-*.tmp").Length > 0 || writing.IsCompleted, TimeSpan.FromSeconds(5));
                AssertTrue(staging); AssertFalse(writing.IsCompleted); stop.Cancel();
                try { await writing; throw new InvalidOperationException("Disk read cancellation ignored."); }
                catch (OperationCanceledException) { }
                AssertFalse(File.Exists(cancelled)); AssertEqual(0, Directory.GetFiles(root, ".nexa-log-export-*.tmp").Length);
                File.WriteAllText(archive, "[12:34:56.789] [Info] [Launch] private-text\n");
                string impossible = Path.Combine(root, "existing-directory.zip"); Directory.CreateDirectory(impossible);
                try { await sink.ExportAsync(impossible); throw new InvalidOperationException("Directory replaced by an export."); }
                catch (IOException) { }
                AssertTrue(Directory.Exists(impossible)); AssertEqual(0, Directory.GetFiles(root, ".nexa-log-export-*.tmp").Length);
            }
            finally { await sink.DisposeAsync(); }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask DiskLogExportSnapshotSurvivesConcurrentRotationAndPruning()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "launcher.log");
            var sink = new FileLogSink(path, 512);
            var entry = new LogEntry(1, DateTimeOffset.UtcNow, LogLevel.Warn, "Launch", "", null);
            try
            {
                for (int index = 0; index < 20; index++)
                    sink.Write(entry, $"[01:00:00.{index:D3}] [Warn] [Launch] old admitted record " + new string('x', 40));
                using var captured = await sink.CaptureSnapshotAsync();
                var capturedPaths = new OwnedLogFiles(path).Archives().Select(file => file.Path).ToArray();
                AssertTrue(capturedPaths.Length > 0);
                AssertTrue(captured.Files.Any(file => file.Current));
                // Force rotation of the captured current file and physical pruning of every old archive.
                for (int index = 0; index < 600; index++)
                    sink.Write(entry, "[02:00:00.000] [Info] [Launch] later record " + new string('x', 70));
                using (await sink.CaptureSnapshotAsync()) { }
                AssertTrue(capturedPaths.All(file => !File.Exists(file)));
                AssertEqual(32, new OwnedLogFiles(path).Archives().Count);
                string target = Path.Combine(root, "barrier.zip");
                await DiskLogExport.WriteAsync(target, captured, CancellationToken.None);
                using (var zip = ZipFile.OpenRead(target))
                {
                    using var reader = new StreamReader(zip.GetEntry("logs.json")!.Open());
                    using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
                    var records = json.RootElement.GetProperty("entries");
                    AssertEqual(20, records.GetArrayLength());
                    var times = records.EnumerateArray().Select(record => record.GetProperty("local_time").GetString()).ToHashSet();
                    for (int index = 0; index < 20; index++)
                        AssertTrue(times.Contains($"01:00:00.{index:D3}"));
                    AssertFalse(json.RootElement.GetProperty("records_truncated").GetBoolean());
                }
                var capturedHandles = captured.Files.Select(file => file.Stream.SafeFileHandle).ToArray();
                captured.Dispose();
                AssertTrue(capturedHandles.All(handle => handle.IsClosed));
                // Cancellation before writing also releases the caller-owned snapshot handles.
                var cancelledSnapshot = await sink.CaptureSnapshotAsync();
                var cancelledHandles = cancelledSnapshot.Files.Select(file => file.Stream.SafeFileHandle).ToArray();
                using var stop = new CancellationTokenSource(); stop.Cancel();
                try
                {
                    using (cancelledSnapshot)
                        await DiskLogExport.WriteAsync(Path.Combine(root, "cancelled-snapshot.zip"), cancelledSnapshot, stop.Token);
                    throw new InvalidOperationException("Snapshot cancellation ignored.");
                }
                catch (OperationCanceledException) { }
                AssertTrue(cancelledHandles.All(handle => handle.IsClosed));
                AssertFalse(File.Exists(Path.Combine(root, "cancelled-snapshot.zip")));
            }
            finally { await sink.DisposeAsync(); }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask DiskLogExportBudgetRejectionPreservesLiveSink()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "launcher.log"), unrelated = Path.Combine(root, "personal.log");
            long oversizedBytes = 69L * 1024 * 1024;
            using (var file = File.Create(path)) file.SetLength(oversizedBytes);
            File.WriteAllText(unrelated, "unrelated private text");
            var sink = new FileLogSink(path);
            try
            {
                string rejected = Path.Combine(root, "oversized.zip");
                try
                {
                    await sink.ExportAsync(rejected).WaitAsync(TimeSpan.FromSeconds(5));
                    throw new InvalidOperationException("Oversized export input accepted.");
                }
                catch (InvalidDataException) { }
                AssertFalse(File.Exists(rejected));
                AssertEqual(0, Directory.GetFiles(root, ".nexa-log-export-*.tmp").Length);
                AssertEqual(oversizedBytes, new FileInfo(path).Length);
                AssertEqual("unrelated private text", File.ReadAllText(unrelated));

                var entry = new LogEntry(1, DateTimeOffset.UtcNow, LogLevel.Info, "Launch", "worker still alive", null);
                sink.Write(entry, entry.ToDisplayText());
                string recovered = Path.Combine(root, "recovered.zip");
                await sink.ExportAsync(recovered).WaitAsync(TimeSpan.FromSeconds(5));
                AssertTrue(new FileInfo(path).Length <= FileLogSink.MaximumFileBytes);
                var archives = new OwnedLogFiles(path).Archives();
                AssertTrue(archives.Count <= OwnedLogFiles.MaximumArchiveCount);
                AssertTrue(archives.Sum(file => file.Length) <= OwnedLogFiles.MaximumArchiveBytes);
                AssertEqual("unrelated private text", File.ReadAllText(unrelated));
                using var zip = ZipFile.OpenRead(recovered);
                using var reader = new StreamReader(zip.GetEntry("logs.json")!.Open());
                using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
                AssertEqual(1, json.RootElement.GetProperty("entries").GetArrayLength());
                AssertEqual("Launch", json.RootElement.GetProperty("entries")[0].GetProperty("module").GetString());
            }
            finally { await sink.DisposeAsync(); }
        }
        finally { Directory.Delete(root, true); }
    }
}
