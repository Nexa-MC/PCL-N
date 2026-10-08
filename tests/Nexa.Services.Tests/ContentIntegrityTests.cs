using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static InstanceContentIntegrityQuery IntegrityFixtureQuery(string instance, string path)
    { var info = new FileInfo(path); return new(instance, "mods", info.Name, info.Length, info.LastWriteTimeUtc.Ticks); }

    private static async ValueTask ContentIntegrityReadsActualHashesAndOnlyManagedBaselines()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root), directory = Path.Combine(instance, "mods"), path = Path.Combine(directory, "a.jar"); Directory.CreateDirectory(directory);
            byte[] before = Encoding.UTF8.GetBytes("before"), after = Encoding.UTF8.GetBytes("after!"); await File.WriteAllBytesAsync(path, before);
            var initial = await InstanceContentIntegrityService.ReadAsync(IntegrityFixtureQuery(instance, path));
            AssertEqual(Convert.ToHexString(SHA256.HashData(before)), initial.Sha256); AssertEqual(Convert.ToHexString(SHA512.HashData(before)), initial.Sha512);
            AssertEqual(InstanceContentBaselineState.Missing, initial.BaselineState); AssertEqual<bool?>(null, initial.Modified);
            AssertFalse(Directory.Exists(Path.Combine(instance, ".nexa-content-updates")));
            string stage = Path.Combine(instance, ".nexa-resource-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage); string download = Path.Combine(stage, "a.jar"); await File.WriteAllBytesAsync(download, after);
            var captured = IntegrityFixtureQuery(instance, path); var store = ContentCompletionStore();
            await InstanceContentUpdateTransaction.ApplyAsync([new(new(instance, "mods", "a.jar", false, captured.ExpectedSize, captured.ExpectedModifiedUtcTicks), download, "a.jar")], store);
            string journalRoot = Path.Combine(instance, ".nexa-content-updates"); string transactionDirectory = Directory.EnumerateDirectories(journalRoot).Single(); Guid id = Guid.ParseExact(Path.GetFileName(transactionDirectory), "N");
            var query = IntegrityFixtureQuery(instance, path); var fact = await InstanceContentIntegrityService.ReadAsync(query);
            AssertEqual(InstanceContentBaselineState.ManagedUpdate, fact.BaselineState); AssertEqual<bool?>(false, fact.Modified); AssertEqual<Guid?>(id, fact.BaselineTransactionId);
            AssertEqual(Convert.ToHexString(SHA256.HashData(after)), fact.BaselineSha256);
            // Same size and restored mtime must still produce the current bytes, never a cached digest.
            await File.WriteAllTextAsync(path, "edited"); File.SetLastWriteTimeUtc(path, new DateTime(query.ExpectedModifiedUtcTicks, DateTimeKind.Utc));
            fact = await InstanceContentIntegrityService.ReadAsync(query); AssertEqual<bool?>(true, fact.Modified);
            AssertEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("edited"))), fact.Sha256);
            await File.WriteAllBytesAsync(path, after);
            AssertTrue((await InstanceContentUpdateTransaction.RollbackAsync(new(instance, id), store)).IsSuccess);
            fact = await InstanceContentIntegrityService.ReadAsync(IntegrityFixtureQuery(instance, path));
            AssertEqual<bool?>(false, fact.Modified); AssertEqual(Convert.ToHexString(SHA256.HashData(before)), fact.BaselineSha256);
            // Another instance's persisted record cannot supply this instance's baseline.
            string foreign = Path.Combine(journalRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(foreign);
            var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(transactionDirectory, "record.json")))!.AsObject(); json["instance"] = Path.Combine(root, "versions", "foreign");
            json["entries"]![0]!["before"] = new string('A', 64); await File.WriteAllTextAsync(Path.Combine(foreign, "record.json"), json.ToJsonString());
            fact = await InstanceContentIntegrityService.ReadAsync(IntegrityFixtureQuery(instance, path)); AssertEqual<bool?>(false, fact.Modified); AssertEqual<Guid?>(id, fact.BaselineTransactionId);
            json["instance"] = instance; json["phase"] = "applying"; await File.WriteAllTextAsync(Path.Combine(foreign, "record.json"), json.ToJsonString());
            File.SetLastWriteTimeUtc(Path.Combine(foreign, "record.json"), DateTime.UtcNow.AddSeconds(1));
            fact = await InstanceContentIntegrityService.ReadAsync(IntegrityFixtureQuery(instance, path)); AssertEqual(InstanceContentBaselineState.Incomplete, fact.BaselineState); AssertEqual<bool?>(null, fact.Modified);
            // Corrupt owned records preserve the SHA fact but make baseline certainty unavailable.
            foreach (string malformed in new[] { "{}", "[]", "broken" })
            {
                await File.WriteAllTextAsync(Path.Combine(foreign, "record.json"), malformed);
                fact = await InstanceContentIntegrityService.ReadAsync(IntegrityFixtureQuery(instance, path)); AssertEqual(InstanceContentBaselineState.Incomplete, fact.BaselineState); AssertEqual<bool?>(null, fact.Modified);
                AssertEqual(Convert.ToHexString(SHA256.HashData(before)), fact.Sha256);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask ContentIntegrityRejectsStaleLinksTraversalBudgetAndCancellation()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root), directory = Path.Combine(instance, "mods"), path = Path.Combine(directory, "a.jar"); Directory.CreateDirectory(directory); await File.WriteAllTextAsync(path, "fixture");
            var query = IntegrityFixtureQuery(instance, path); byte[] original = await File.ReadAllBytesAsync(path);
            foreach (var invalid in new[] { query with { Name = "../a.jar" }, query with { PageId = "config" }, query with { ExpectedSize = InstanceContentIntegrityService.MaximumFileBytes + 1 }, query with { ExpectedSize = query.ExpectedSize + 1 } })
            {
                bool rejected = false; try { await InstanceContentIntegrityService.ReadAsync(invalid); } catch (Exception error) when (error is IOException or InvalidDataException) { rejected = true; }
                AssertTrue(rejected);
            }
            File.SetLastWriteTimeUtc(path, new DateTime(query.ExpectedModifiedUtcTicks, DateTimeKind.Utc).AddSeconds(1));
            bool stale = false; try { await InstanceContentIntegrityService.ReadAsync(query); } catch (IOException) { stale = true; }
            AssertTrue(stale);
            using var stop = new CancellationTokenSource(); stop.Cancel(); bool cancelled = false;
            try { await InstanceContentIntegrityService.ReadAsync(IntegrityFixtureQuery(instance, path), stop.Token); } catch (OperationCanceledException) { cancelled = true; }
            AssertTrue(cancelled);
            if (!OperatingSystem.IsWindows())
            {
                string link = Path.Combine(directory, "link.jar"); File.CreateSymbolicLink(link, path); var info = new FileInfo(link); bool rejected = false;
                try { await InstanceContentIntegrityService.ReadAsync(new(instance, "mods", "link.jar", info.Length, info.LastWriteTimeUtc.Ticks)); } catch (IOException) { rejected = true; }
                AssertTrue(rejected); File.Delete(link);
            }
            string journal = Path.Combine(instance, ".nexa-content-updates"); Directory.CreateDirectory(journal);
            for (int index = 0; index < 1001; index++) Directory.CreateDirectory(Path.Combine(journal, Guid.NewGuid().ToString("N")));
            var fact = await InstanceContentIntegrityService.ReadAsync(IntegrityFixtureQuery(instance, path)); AssertEqual(InstanceContentBaselineState.Incomplete, fact.BaselineState); AssertEqual<bool?>(null, fact.Modified);
            byte[] preserved = await File.ReadAllBytesAsync(path); AssertTrue(original.SequenceEqual(preserved));
        }
        finally { Directory.Delete(root, true); }
    }
}
