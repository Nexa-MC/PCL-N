using System.IO.Compression;
using System.Text.Json;
using Nexa.Services.Logging;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask DiagnosticBundleExcludesPersonalTextAndPublishesAtomically()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-diagnostic-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string target = Path.Combine(root, "report.zip");
            using LogService log = CreateLogService();
            using (LogOperation operation = log.BeginOperation("Install", "Prepare", "UnlabeledSecret AccountName C:\\Users\\Alice /home/alice/private"))
            {
                operation.Stage("download", "accessToken=another-secret");
                operation.Complete("private-completion-text");
            }
            log.Error("Install", "unlabeled-token /Users/Alice/Documents", "raw-stack-with-private-data");
            var snapshot = new DiagnosticBundleSnapshot("2.0.0.alpha.5", "Linux", "X64")
            {
                Mods = [new("sodium", "0.5.0", true), new("../../personal", "bad\nvalue", false)],
                Logs = log.GetSnapshot(),
                FailedTasks = 1,
                InventoryComplete = false
            };
            await DiagnosticBundle.WriteAsync(target, snapshot);
            using (ZipArchive archive = ZipFile.OpenRead(target))
            {
                AssertEqual(3, archive.Entries.Count);
                var texts = new List<string>();
                foreach (var entry in archive.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    texts.Add(await reader.ReadToEndAsync());
                }
                string all = string.Join('\n', texts);
                foreach (string secret in new[] { "UnlabeledSecret", "AccountName", "Alice", "/home/", "another-secret", "private-completion-text", "unlabeled-token", "raw-stack", "../../personal", "bad\\nvalue" })
                    AssertFalse(all.Contains(secret, StringComparison.Ordinal));
                AssertTrue(all.Contains("Prepare"));
                AssertTrue(all.Contains("download"));
                AssertTrue(all.Contains("Completed"));
                AssertTrue(all.Contains("sodium"));
                using JsonDocument summary = JsonDocument.Parse(texts[0]);
                AssertEqual(JsonValueKind.Null, summary.RootElement.GetProperty("integrity_verified_files").ValueKind);
                AssertFalse(summary.RootElement.GetProperty("inventory_complete").GetBoolean());
            }
            byte[] before = await File.ReadAllBytesAsync(target);
            try { await DiagnosticBundle.WriteAsync(target, snapshot); throw new InvalidOperationException("Existing export overwritten."); }
            catch (IOException) { }
            byte[] after = await File.ReadAllBytesAsync(target);
            AssertTrue(before.SequenceEqual(after));
            using CancellationTokenSource stop = new();
            stop.Cancel();
            try { await DiagnosticBundle.WriteAsync(Path.Combine(root, "cancelled.zip"), snapshot, stop.Token); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            AssertFalse(File.Exists(Path.Combine(root, "cancelled.zip")));
            AssertEqual(1, Directory.GetFiles(root).Length);
            var large = snapshot with { Mods = Enumerable.Range(0, DiagnosticBundle.MaximumMods + 1).Select(i => new DiagnosticModIdentity("mod-" + i, "1", true)).ToArray() };
            string bounded = Path.Combine(root, "bounded.zip");
            await DiagnosticBundle.WriteAsync(bounded, large);
            AssertTrue(new FileInfo(bounded).Length < DiagnosticBundle.MaximumBytes);
            using var boundedZip = ZipFile.OpenRead(bounded);
            using var summaryReader = new StreamReader(boundedZip.GetEntry("summary.json")!.Open());
            using var boundedSummary = JsonDocument.Parse(await summaryReader.ReadToEndAsync());
            AssertTrue(boundedSummary.RootElement.GetProperty("mods_truncated").GetBoolean());
            // The aggregate uncompressed budget must hold even when compression is excellent.
            string longIdentifier = new('a', 128);
            var oversized = snapshot with
            {
                Mods = Enumerable.Repeat(new DiagnosticModIdentity(longIdentifier, longIdentifier, true), DiagnosticBundle.MaximumMods).ToArray(),
                Logs = Enumerable.Repeat(new LogEntry(1, DateTimeOffset.UtcNow, LogLevel.Info, longIdentifier, "", null)
                { Operation = new(longIdentifier, longIdentifier, DiagnosticOperationOutcome.Completed) }, DiagnosticBundle.MaximumLogs).ToArray()
            };
            try { await DiagnosticBundle.WriteAsync(Path.Combine(root, "oversized.zip"), oversized); throw new InvalidOperationException("Content budget ignored."); }
            catch (InvalidDataException) { }
            AssertFalse(File.Exists(Path.Combine(root, "oversized.zip")));
            AssertEqual(2, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, true); }
    }
}
