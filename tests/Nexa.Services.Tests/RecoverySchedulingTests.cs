using System.Text;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Scheduling;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask RecoverySourceChunksYieldWhenQuietBeginsMidFile()
    {
        string root = CreateTempDirectory();
        try
        {
            using WorkScheduler work = new(new(1, 1, 1));
            var blobs = new RecoveryBlobStore(root);
            byte[] data = new byte[256 * 1024]; new Random(733).NextBytes(data);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                IWorkQuietLease? quiet = null;
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var source = new QuietRecoveryStream(data, () =>
                {
                    quiet = work.EnterQuiet(); started.SetResult();
                });
                using var stop = new CancellationTokenSource();
                Task<RecoveryBlob> storing;
                using (work.UsePriority(WorkPriority.Critical))
                    storing = blobs.StoreAsync(source, data.Length, new(data.Length), work, stop.Token);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (work.Snapshot.Resources.All(r => r.Waiting == 0)) await Task.Delay(1, timeout.Token);
                AssertFalse(storing.IsCompleted);
                AssertEqual(1, source.Reads);
                AssertTrue(source.MaximumRead <= 81920);
                using (IDisposable foreground = await work.AcquireAsync(WorkPriority.Critical,
                    WorkResource.Cpu | WorkResource.Disk).AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
                    AssertEqual(1, source.Reads);
                if (attempt == 0)
                {
                    stop.Cancel();
                    try { await storing; throw new InvalidOperationException("Paused source ignored cancellation."); }
                    catch (OperationCanceledException) { }
                    AssertEqual(0, Directory.GetFiles(Path.Combine(root, "objects"), "*.br").Length);
                    quiet!.Dispose();
                }
                else
                {
                    quiet!.Dispose();
                    var blob = await storing.WaitAsync(TimeSpan.FromSeconds(10));
                    await blobs.CopyVerifiedAsync(blob, Stream.Null, new(data.Length));
                    AssertTrue(source.Reads > 1);
                    AssertTrue(source.MaximumRead <= 81920);
                    AssertEqual(1, Directory.GetFiles(Path.Combine(root, "objects"), "*.br").Length);
                }
                AssertEqual(0, Directory.GetFiles(Path.Combine(root, "objects"), "*.part").Length);
                AssertTrue(work.Snapshot.Resources.All(r => r.Active == 0 && r.Waiting == 0));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask RecoveryCaptureAdmissionPreservesBaselineAcrossQuietCancellation()
    {
        string root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "file.txt"), "first");
            RecoverySource[] sources = [new("instance", "file.txt")];
            var originalStore = new RecoverySnapshotStore(root, root);
            var first = await originalStore.CaptureAsync(sources, "{}");
            using WorkScheduler work = new(new(1, 1, 1));
            var scheduled = new RecoverySnapshotStore(root, root, work);
            File.WriteAllText(Path.Combine(root, "file.txt"), "second");
            using IWorkQuietLease quiet = work.EnterQuiet();
            using var stop = new CancellationTokenSource();
            Task<RecoverySnapshot> pending = scheduled.CaptureAsync(sources, "{}", stop.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (work.Snapshot.Resources.All(r => r.Waiting == 0)) await Task.Delay(1, timeout.Token);
            AssertFalse(pending.IsCompleted);
            AssertEqual(1, work.Snapshot.Resources.Single(r => r.Resource == WorkResource.Cpu).Waiting);
            AssertEqual(1, Directory.GetFiles(Path.Combine(root, "Nexa", "Recovery", "objects"), "*.br").Length);
            stop.Cancel();
            try { await pending; throw new InvalidOperationException("Paused capture ignored cancellation."); }
            catch (OperationCanceledException) { }
            AssertEqual(first.Revision, (await originalStore.ReadAsync())!.Revision);
            AssertTrue(work.Snapshot.Resources.All(r => r.Active == 0 && r.Waiting == 0));
            pending = scheduled.CaptureAsync(sources, "{}");
            quiet.Dispose();
            var second = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            AssertFalse(first.Revision == second.Revision);
            AssertEqual(1, Directory.GetFiles(Path.Combine(root, "Nexa", "Recovery", "objects"), "*.br").Length);

            File.WriteAllText(Path.Combine(root, "file.txt"), "third");
            IWorkQuietLease? cleanupQuiet = null;
            var third = await scheduled.CaptureAsync(sources, "{}", _ =>
            {
                cleanupQuiet = work.EnterQuiet();
                return Task.CompletedTask;
            }, retainHistory: true);
            AssertEqual(third.Revision, (await originalStore.ReadAsync())!.Revision);
            AssertEqual(2, (await originalStore.ListAsync()).Count);
            AssertEqual(2, Directory.GetFiles(Path.Combine(root, "Nexa", "Recovery", "objects"), "*.br").Length);
            AssertTrue(work.Snapshot.Resources.All(r => r.Active == 0 && r.Waiting == 0));
            cleanupQuiet!.Dispose();

            // Cancellation after the atomic rename defers GC, never rewinds a successful commit.
            using var afterCommit = new CancellationTokenSource();
            var hook = new RecoveryMaintenanceHook(work) { BeforeTry = () => afterCommit.Cancel() };
            var committed = await new RecoverySnapshotStore(root, root, hook).CaptureAsync(sources, "{}", afterCommit.Token);
            AssertEqual(committed.Revision, (await originalStore.ReadAsync())!.Revision);
            AssertTrue(work.Snapshot.Resources.All(r => r.Active == 0 && r.Waiting == 0));
            work.Dispose();
            await new RecoveryBlobStore(Path.Combine(root, "Nexa", "Recovery")).CollectUnreferencedAsync(
                committed.Files.Select(f => f.Blob.Sha256).ToHashSet(StringComparer.Ordinal), default, work);
            AssertEqual(committed.Revision, (await originalStore.ReadAsync())!.Revision);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask RecoveryMaintenanceChunksDeferAndRetainJournalObjects()
    {
        string root = CreateTempDirectory();
        try
        {
            var blobs = new RecoveryBlobStore(root);
            RecoveryBlob? retainedBlob = null;
            for (int i = 0; i < 100; i++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes("object-" + i);
                using var source = new MemoryStream(bytes);
                var blob = await blobs.StoreAsync(source, bytes.Length, new(bytes.Length));
                retainedBlob ??= blob;
            }
            var retained = new HashSet<string>(StringComparer.Ordinal) { retainedBlob!.Sha256 };
            string objects = Path.Combine(root, "objects");
            using WorkScheduler work = new(new(1, 1, 1));
            using (work.EnterQuiet())
            {
                await blobs.CollectUnreferencedAsync(retained, default, work);
                AssertEqual(100, Directory.GetFiles(objects, "*.br").Length);
                AssertTrue(work.Snapshot.Resources.All(r => r.Active == 0 && r.Waiting == 0));
            }
            using (IDisposable busy = await work.AcquireAsync(WorkPriority.Critical, WorkResource.Disk))
            {
                await blobs.CollectUnreferencedAsync(retained, default, work);
                AssertEqual(100, Directory.GetFiles(objects, "*.br").Length);
                AssertEqual(0, work.Snapshot.Resources.Single(r => r.Resource == WorkResource.Cpu).Active);
            }
            await blobs.CollectUnreferencedAsync(retained, default, new LegacyMaintenanceScheduler(work));
            AssertEqual(100, Directory.GetFiles(objects, "*.br").Length);
            string transactions = Path.Combine(root, "transactions"); Directory.CreateDirectory(transactions);
            await blobs.CollectUnreferencedAsync(retained, default, work);
            AssertEqual(100, Directory.GetFiles(objects, "*.br").Length);
            Directory.Delete(transactions);

            IWorkQuietLease? quiet = null;
            int admissions = 0;
            var hook = new RecoveryMaintenanceHook(work)
            {
                BeforeTry = () => { if (++admissions == 3) quiet = work.EnterQuiet(); }
            };
            await blobs.CollectUnreferencedAsync(retained, default, hook);
            int remaining = Directory.GetFiles(objects, "*.br").Length;
            AssertTrue(remaining is 68 or 69); // One 32-entry chunk, with/without the retained entry.
            AssertTrue(File.Exists(Path.Combine(objects, retainedBlob.Sha256 + ".br")));
            AssertTrue(work.Snapshot.Resources.All(r => r.Active == 0 && r.Waiting == 0));
            quiet!.Dispose();
            // Both file locks were released despite early exit; retry completes deletion.
            await blobs.CollectUnreferencedAsync(retained, default, work).WaitAsync(TimeSpan.FromSeconds(10));
            AssertEqual(1, Directory.GetFiles(objects, "*.br").Length);
            await blobs.CopyVerifiedAsync(retainedBlob, Stream.Null, new(retainedBlob.Length));
            AssertTrue(work.Snapshot.Resources.All(r => r.Active == 0 && r.Waiting == 0));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask AutomaticRecoveryYieldsToNewGameOperations()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test"); Directory.CreateDirectory(instance);
            File.WriteAllText(Path.Combine(instance, "test.json"), """{"id":"test","_minecraftVersion":"1.20.1"}""");
            string jar = Path.Combine(instance, "test.jar"); File.WriteAllText(jar, "client");
            var (_, settings) = PolicyFixture();
            var builder = new XsrStateStoreBuilder(); MinecraftProcessStateComposition.DeclareState(builder);
            var state = builder.Build();
            using WorkScheduler work = new(new(1, 1, 1));
            var service = new InstanceRecoveryService(settings, state) { WorkScheduler = work };
            MinecraftLaunchPlan plan = new("java", instance, [], [], [], new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, "example.Main", []))
            { MinecraftRootDirectory = root, InstanceDirectory = instance, GameDirectory = instance, ClientJarPath = jar };
            var session = new MinecraftProcessSnapshot(Guid.NewGuid(), "test", 1, MinecraftProcessState.Exited, 0, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow)
            { InstanceDirectory = instance, GameDirectory = instance, GameWindowConfirmed = true };
            using IWorkQuietLease quiet = work.EnterQuiet();
            Task<bool> capture;
            using (work.UsePriority(WorkPriority.Critical)) capture = service.RecordSuccessfulExitAsync(plan, session, false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (work.Snapshot.Resources.All(r => r.Waiting == 0)) await Task.Delay(1, timeout.Token);
            AssertFalse(capture.IsCompleted);
            AssertFalse(Directory.Exists(Path.Combine(instance, "Nexa", "Recovery")));
            using (var operation = await InstanceRecoveryOperationGate.EnterOperationAsync(root).AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
                AssertFalse(await capture.WaitAsync(TimeSpan.FromSeconds(10)));
            AssertTrue(work.Snapshot.Resources.All(r => r.Active == 0 && r.Waiting == 0));
            quiet.Dispose();
            AssertTrue(await service.RecordSuccessfulExitAsync(plan, session, false).WaitAsync(TimeSpan.FromSeconds(10)));
            AssertEqual(2, (await new RecoverySnapshotStore(instance, instance).ReadAsync())!.Files.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class RecoveryMaintenanceHook(IWorkScheduler inner) : IWorkScheduler
    {
        public Action? BeforeTry { get; init; }
        public WorkPriority CurrentPriority => inner.CurrentPriority;
        public IDisposable UsePriority(WorkPriority priority) => inner.UsePriority(priority);
        public ValueTask<IDisposable> AcquireAsync(WorkPriority priority, WorkResource resource, CancellationToken token = default) => inner.AcquireAsync(priority, resource, token);
        public IWorkQuietLease EnterQuiet() => inner.EnterQuiet();
        public IDisposable? TryAcquire(WorkPriority priority, WorkResource resource, CancellationToken token = default)
        { BeforeTry?.Invoke(); return inner.TryAcquire(priority, resource, token); }
    }

    private sealed class LegacyMaintenanceScheduler(IWorkScheduler inner) : IWorkScheduler
    {
        public WorkPriority CurrentPriority => inner.CurrentPriority;
        public IDisposable UsePriority(WorkPriority priority) => inner.UsePriority(priority);
        public ValueTask<IDisposable> AcquireAsync(WorkPriority priority, WorkResource resource, CancellationToken token = default) => inner.AcquireAsync(priority, resource, token);
        public IWorkQuietLease EnterQuiet() => inner.EnterQuiet();
    }

    private sealed class QuietRecoveryStream(byte[] bytes, Action firstRead) : MemoryStream(bytes)
    {
        public int Reads { get; private set; }
        public int MaximumRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            ValueTask<int> read = base.ReadAsync(buffer, token);
            MaximumRead = Math.Max(MaximumRead, buffer.Length);
            if (++Reads == 1) firstRead();
            return read;
        }
    }
}
