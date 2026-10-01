using System.IO.Compression;
using Nexa.Services.Downloads;
using Nexa.Services.Files;
using Nexa.Services.Logging;
using Nexa.Services.Minecraft.Downloads;
using Nexa.Services.Minecraft.Management;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask FileWorkersAreBoundedAndJoinCancellation()
    {
        int active = 0, peak = 0, finished = 0;
        var wave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task work = FileBatchProgress.RunAsync(32, async (_, token) =>
        {
            int current = Interlocked.Increment(ref active);
            int old;
            do { old = Volatile.Read(ref peak); if (current <= old) break; }
            while (Interlocked.CompareExchange(ref peak, current, old) != old);
            if (current == 8) wave.TrySetResult();
            try { await release.Task.WaitAsync(token); Interlocked.Increment(ref finished); }
            finally { Interlocked.Decrement(ref active); }
        }, default);
        await wave.Task.WaitAsync(TimeSpan.FromSeconds(10));
        AssertEqual(8, peak);
        release.SetResult(); await work;
        AssertEqual(32, finished); AssertEqual(0, active);
        using var stop = new CancellationTokenSource();
        wave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        work = FileBatchProgress.RunAsync(32, async (_, token) =>
        {
            if (Interlocked.Increment(ref active) == 8) wave.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Interlocked.Decrement(ref active); }
        }, stop.Token);
        await wave.Task.WaitAsync(TimeSpan.FromSeconds(10)); stop.Cancel();
        try { await work; throw new InvalidOperationException("Worker cancellation ignored."); }
        catch (OperationCanceledException) { }
        AssertEqual(0, active);
    }

    private static async ValueTask FileReceiptsInvalidateAndExplicitVerificationHashes()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "library.jar");
            await File.WriteAllTextAsync(path, "REPAIRED");
            var expected = new MinecraftExpectedFile(path, 8, Sha1Hex("REPAIRED"));
            var cache = new MinecraftFileVerificationCache();
            AssertTrue(await cache.VerifyAsync(expected, default));
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                AssertTrue(await cache.VerifyAsync(expected, default));
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            await File.WriteAllTextAsync(path, "CORRUPT!"); File.SetLastWriteTimeUtc(path, stamp.AddSeconds(2));
            AssertFalse(await cache.VerifyAsync(expected, default));
            AssertFalse(await cache.VerifyAsync(expected with { Sha1 = Sha1Hex("different") }, default));
            string asset = Path.Combine(root, Sha1Hex("REPAIRED"));
            await File.WriteAllTextAsync(asset, "CORRUPT!");
            var objectFile = expected with { Path = asset };
            AssertTrue(await cache.VerifyAsync(objectFile, default, contentAddressed: true));
            AssertFalse(await cache.VerifyAsync(objectFile, default, contentAddressed: true, forceHash: true));
            await File.WriteAllTextAsync(asset, "short");
            AssertFalse(await cache.VerifyAsync(objectFile, default, contentAddressed: true));
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { await cache.VerifyAsync(objectFile, stop.Token); throw new InvalidOperationException("Cached verify ignored cancellation."); }
            catch (OperationCanceledException) { }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask SnapshotReusesStampsAndRepairsChangedObjects()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test"); Directory.CreateDirectory(instance);
            string source = Path.Combine(instance, "a.txt"); File.WriteAllText(source, "same-content");
            RecoverySource[] sources = [new("instance", "a.txt")];
            var first = await new RecoverySnapshotStore(instance, instance).CaptureAsync(sources, "{}");
            string blob = Directory.GetFiles(Path.Combine(instance, "Nexa", "Recovery", "objects"), "*.br").Single();
            DateTime original = File.GetLastWriteTimeUtc(blob);
            RecoverySnapshot second;
            using (var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                second = await new RecoverySnapshotStore(instance, instance).CaptureAsync(sources, "{}");
            AssertEqual(original, File.GetLastWriteTimeUtc(blob));
            AssertEqual(first.Files[0].Blob, second.Files[0].Blob);
            File.WriteAllBytes(blob, [0, 1, 2]); File.SetLastWriteTimeUtc(blob, original.AddSeconds(2));
            var repaired = await new RecoverySnapshotStore(instance, instance).CaptureAsync(sources, "{}");
            var blobs = new RecoveryBlobStore(Path.Combine(instance, "Nexa", "Recovery"));
            using var output = new MemoryStream();
            await blobs.CopyVerifiedAsync(repaired.Files[0].Blob, output, new(100));
            AssertEqual("same-content", System.Text.Encoding.UTF8.GetString(output.ToArray()));
            File.WriteAllText(source, "new content");
            var changed = await new RecoverySnapshotStore(instance, instance).CaptureAsync(sources, "{}");
            AssertFalse(changed.Files[0].Blob == repaired.Files[0].Blob);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask MetadataCacheKeepsBudgetAndInvalidatesFileStamp()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "test.jar");
            void Write(string name)
            {
                using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
                using var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open());
                writer.Write("{\"id\":\"test\",\"name\":\"" + name + "\",\"version\":\"1\"}");
            }
            Write("First");
            var raw = InstanceManagementService.ReadContent(new("mods", "mods", root), default);
            var budget = new ArchiveReadBudget(8 * 1024 * 1024);
            var first = await InstanceContentMetadata.EnrichAsync(raw, root, budget, default);
            long remaining = budget.Remaining;
            budget = new(8 * 1024 * 1024);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var cached = await InstanceContentMetadata.EnrichAsync(raw, root, budget, default);
                AssertEqual("First", cached.Entries[0].DisplayName);
                AssertEqual(remaining, budget.Remaining);
            }
            File.Delete(path); Write("Second"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            var second = await InstanceContentMetadata.EnrichAsync(raw, root, new(8 * 1024 * 1024), default);
            AssertEqual("Second", second.Entries[0].DisplayName);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void BatchedLoggingKeepsBoundedRingAndFlushesState()
    {
        var builder = new XsrStateStoreBuilder(); LogService.DeclareState(builder);
        var store = builder.Build();
        using var log = new LogService(store, 16, null, TimeSpan.FromHours(1));
        for (int index = 0; index < 1000; index++) log.Info("Batch", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var id = store.Resolve(LogService.EntriesKey);
        AssertEqual(0, store.ReadCollection<LogEntry>(id).Count);
        log.FlushPending();
        var snapshot = store.ReadCollection<LogEntry>(id);
        AssertEqual(16, snapshot.Count); AssertEqual(1L, snapshot.Revision);
        AssertEqual(985L, snapshot.Items[0].Sequence); AssertEqual(1000L, snapshot.Items[^1].Sequence);
        log.Clear(); AssertEqual(0, store.ReadCollection<LogEntry>(id).Count);
    }
}
