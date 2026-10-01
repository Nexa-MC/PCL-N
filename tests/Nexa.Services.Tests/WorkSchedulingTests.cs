using System.Net;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Resources;
using Nexa.Services.Scheduling;
using Nexa.Services.Updates;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask OptionalWorkAdmissionNeverQueuesOrPartiallyReserves()
    {
        using WorkScheduler work = new(new(1, 1, 1));
        using (IWorkQuietLease quiet = work.EnterQuiet())
        {
            AssertTrue(work.TryAcquire(WorkPriority.Background, WorkResource.Cpu | WorkResource.Disk) is null);
            AssertTrue(work.TryAcquire(WorkPriority.Idle, WorkResource.Disk) is null);
            using IDisposable foreground = work.TryAcquire(WorkPriority.Critical, WorkResource.Cpu | WorkResource.Disk)!;
            AssertTrue(foreground is not null);
            AssertTrue(work.Snapshot.Resources.All(r => r.Waiting == 0));
        }
        IDisposable disk = await work.AcquireAsync(WorkPriority.Interactive, WorkResource.Disk);
        AssertTrue(work.TryAcquire(WorkPriority.Idle, WorkResource.Cpu | WorkResource.Disk) is null);
        AssertEqual(0, work.Snapshot.Resources.Single(r => r.Resource == WorkResource.Cpu).Active);
        Task<IDisposable> queued = work.AcquireAsync(WorkPriority.Critical, WorkResource.Disk | WorkResource.Http).AsTask();
        using (IDisposable independent = work.TryAcquire(WorkPriority.Interactive, WorkResource.Http)!)
        {
            AssertTrue(independent is not null);
            AssertFalse(queued.IsCompleted);
        }
        disk.Dispose();
        using (IDisposable queuedLease = await queued)
            AssertTrue(work.TryAcquire(WorkPriority.Idle, WorkResource.Disk | WorkResource.Http) is null);
        AssertTrue(work.Snapshot.Resources.All(r => r.Active == 0 && r.Waiting == 0));
        IWorkScheduler legacy = new LegacyMaintenanceScheduler(work);
        AssertTrue(legacy.TryAcquire(WorkPriority.Idle, WorkResource.Cpu) is null);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        AssertThrows<OperationCanceledException>(() => work.TryAcquire(WorkPriority.Idle, WorkResource.Cpu, stop.Token));
        AssertThrows<OperationCanceledException>(() => legacy.TryAcquire(WorkPriority.Idle, WorkResource.Cpu, stop.Token));
        AssertThrows<ArgumentOutOfRangeException>(() => work.TryAcquire((WorkPriority)99, WorkResource.Cpu));
        AssertThrows<ArgumentOutOfRangeException>(() => work.TryAcquire(WorkPriority.Idle, (WorkResource)0));
        AssertThrows<ArgumentOutOfRangeException>(() => legacy.TryAcquire(WorkPriority.Idle, (WorkResource)8));
        work.Dispose();
        AssertThrows<ObjectDisposedException>(() => work.TryAcquire(WorkPriority.Idle, WorkResource.Cpu));
        AssertTrue(work.Snapshot.Resources.All(r => r.Active == 0 && r.Waiting == 0));
    }

    private static async ValueTask WorkAdmissionIsAtomicBoundedAndCancellable()
    {
        using WorkScheduler work = new(new(1, 1, 1, 8));
        using IDisposable disk = await work.AcquireAsync(WorkPriority.Interactive, WorkResource.Disk);
        using var stop = new CancellationTokenSource();
        Task<IDisposable> composite = work.AcquireAsync(WorkPriority.Critical, WorkResource.Disk | WorkResource.Http, stop.Token).AsTask();
        using (IDisposable http = await work.AcquireAsync(WorkPriority.Interactive, WorkResource.Http))
        {
            AssertFalse(composite.IsCompleted);
            AssertEqual(1, work.Snapshot.Resources.Single(r => r.Resource == WorkResource.Http).Active);
        }
        stop.Cancel();
        try { await composite; throw new InvalidOperationException("Composite admission ignored cancellation."); }
        catch (OperationCanceledException) { }
        AssertTrue(work.Snapshot.Resources.All(r => r.Waiting == 0));
        using IWorkQuietLease quiet = work.EnterQuiet();
        Task<IDisposable>[] background = Enumerable.Range(0, 6)
            .Select(_ => work.AcquireAsync(WorkPriority.Background, WorkResource.Cpu).AsTask()).ToArray();
        AssertThrows<InvalidOperationException>(() => work.AcquireAsync(WorkPriority.Idle, WorkResource.Cpu).AsTask().GetAwaiter().GetResult().Dispose());
        using (IDisposable foreground = await work.AcquireAsync(WorkPriority.Critical, WorkResource.Cpu))
            AssertFalse(background.Any(t => t.IsCompleted));
        quiet.Dispose();
        foreach (Task<IDisposable> task in background) (await task.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
        AssertTrue(work.Snapshot.Resources.All(r => r.Waiting == 0));
    }

    private static async ValueTask WorkAdmissionWeightsPreventStarvationAndDisposeWaiters()
    {
        using WorkScheduler work = new(new(1, 1, 1));
        IDisposable first = await work.AcquireAsync(WorkPriority.Critical, WorkResource.Cpu);
        Task<IDisposable>[] critical = Enumerable.Range(0, 16).Select(_ => work.AcquireAsync(WorkPriority.Critical, WorkResource.Cpu).AsTask()).ToArray();
        Task<IDisposable>[] interactive = Enumerable.Range(0, 4).Select(_ => work.AcquireAsync(WorkPriority.Interactive, WorkResource.Cpu).AsTask()).ToArray();
        Task<IDisposable> background = work.AcquireAsync(WorkPriority.Background, WorkResource.Cpu).AsTask();
        Task<IDisposable> idle = work.AcquireAsync(WorkPriority.Idle, WorkResource.Cpu).AsTask();
        first.Dispose();
        for (int i = 0; i < 8; i++) (await critical[i]).Dispose();
        AssertFalse(critical[8].IsCompleted);
        foreach (Task<IDisposable> task in interactive) (await task).Dispose();
        (await background).Dispose(); (await idle).Dispose();
        AssertTrue(critical[8].IsCompleted);
        (await critical[8]).Dispose();
        work.Dispose();
        // The currently admitted lease remains owned by its caller after shutdown.
        (await critical[9]).Dispose();
        foreach (Task<IDisposable> task in critical.Skip(10))
        {
            try { await task; throw new InvalidOperationException("Shutdown retained a pending admission."); }
            catch (ObjectDisposedException) { }
        }
        AssertTrue(work.Snapshot.Resources.All(r => r.Waiting == 0 && r.Active == 0));
        AssertThrows<ObjectDisposedException>(() => work.AcquireAsync(WorkPriority.Critical, WorkResource.Cpu).AsTask().GetAwaiter().GetResult().Dispose());
    }

    private static async ValueTask WorkQuietScopesRespectGraceAndPriorityContext()
    {
        using SchedulingClock clock = new();
        XsrStateStoreBuilder builder = new(); WorkSchedulingContract.DeclareState(builder);
        XsrStateStore store = builder.Build();
        using WorkScheduler work = new(store: store, clock: clock);
        AssertEqual(WorkPriority.Interactive, work.CurrentPriority);
        using (work.UsePriority(WorkPriority.Critical))
        {
            await Task.Yield(); AssertEqual(WorkPriority.Critical, work.CurrentPriority);
            AssertEqual(WorkPriority.Critical, await Task.Run(() => work.CurrentPriority));
            using (work.UsePriority(WorkPriority.Idle)) AssertEqual(WorkPriority.Idle, work.CurrentPriority);
            AssertEqual(WorkPriority.Critical, work.CurrentPriority);
        }
        AssertEqual(WorkPriority.Interactive, work.CurrentPriority);
        IWorkQuietLease first = work.EnterQuiet(); IWorkQuietLease second = work.EnterQuiet();
        Task<IDisposable> pending = work.AcquireAsync(WorkPriority.Background, WorkResource.Http).AsTask();
        first.ReleaseAfter(TimeSpan.FromSeconds(15));
        clock.Advance(TimeSpan.FromSeconds(14)); AssertEqual(2, work.Snapshot.QuietScopes);
        clock.Advance(TimeSpan.FromSeconds(1)); AssertEqual(1, work.Snapshot.QuietScopes);
        AssertFalse(pending.IsCompleted); second.Dispose();
        (await pending).Dispose();
        AssertFalse(store.Read<WorkQuietSnapshot>(store.Resolve(WorkSchedulingContract.QuietKey)).Value!.IsQuiet);
        IWorkQuietLease retired = work.EnterQuiet(); retired.ReleaseAfter(TimeSpan.FromSeconds(15));
        retired.Dispose(); AssertEqual(0, clock.ActiveTimers);
        retired = work.EnterQuiet(); retired.ReleaseAfter(TimeSpan.FromSeconds(15));
        work.Dispose(); AssertEqual(0, clock.ActiveTimers); AssertEqual(0, work.Snapshot.QuietScopes);
    }

    private static async ValueTask InstallPrefetchYieldsAndExplicitReadPromotes()
    {
        using WorkScheduler work = new();
        using IWorkQuietLease quiet = work.EnterQuiet();
        XsrStateStoreBuilder builder = new(); InstallCatalogStateContract.DeclareState(builder);
        SchedulingCatalogSource source = new();
        using InstallCatalogService service = new(builder.Build(), source) { WorkScheduler = work };
        Task prefetch = service.PrefetchAsync(new("1.20.1"), default);
        await service.ReadAsync(new("1.20.1", InstallLoader.Fabric), default).WaitAsync(TimeSpan.FromSeconds(10));
        AssertEqual(1, source.Calls); AssertFalse(prefetch.IsCompleted);
        quiet.Dispose(); await prefetch.WaitAsync(TimeSpan.FromSeconds(10));
        AssertTrue(source.Calls > 1); AssertTrue(work.Snapshot.Resources.All(r => r.Active == 0 && r.Waiting == 0));
    }

    private static async ValueTask UpdateDiscoveryYieldsAndCancelsWithoutNetwork()
    {
        using WorkScheduler work = new(); using IWorkQuietLease quiet = work.EnterQuiet();
        SchedulingUpdateHandler handler = new(); using HttpClient http = new(handler);
        NexaUpdateService updates = new(http) { WorkScheduler = work };
        using var stop = new CancellationTokenSource();
        Task<Nexa.Xsr.XsrResult<NexaUpdateStatus>> check = updates.CheckAsync(new("2.0.0.alpha.5", "linux-x64", "alpha"), stop.Token);
        AssertFalse(check.IsCompleted); AssertEqual(0, handler.Calls);
        stop.Cancel(); AssertFalse((await check).IsSuccess); AssertEqual(0, handler.Calls);
        check = updates.CheckAsync(new("2.0.0.alpha.5", "linux-x64", "alpha"));
        quiet.Dispose(); AssertTrue((await check).IsSuccess); AssertEqual(1, handler.Calls);
    }

    private static async ValueTask WorkCancellationRacesNeverRetainResourceLeases()
    {
        using WorkScheduler work = new(new(1, 1, 1));
        for (int i = 0; i < 200; i++)
        {
            IDisposable active = await work.AcquireAsync(WorkPriority.Critical, WorkResource.Cpu);
            using var stop = new CancellationTokenSource();
            Task<IDisposable> pending = work.AcquireAsync(WorkPriority.Interactive, WorkResource.Cpu, stop.Token).AsTask();
            await Task.WhenAll(Task.Run(active.Dispose), Task.Run(() => stop.Cancel()));
            try { (await pending).Dispose(); } catch (OperationCanceledException) { }
            AssertTrue(work.Snapshot.Resources.All(r => r.Active == 0 && r.Waiting == 0));
        }
    }

    private static async ValueTask ScheduledDownloadsInheritCriticalAndReleaseAfterFailure()
    {
        using WorkScheduler work = new(new(1, 1, 1)); using IWorkQuietLease quiet = work.EnterQuiet();
        XsrStateStoreBuilder builder = new(); DownloadService.DeclareState(builder);
        DownloadService downloads = new(builder.Build(), minimumSegmentBytes: 16) { WorkScheduler = work };
        string root = CreateTempDirectory();
        try
        {
            using (work.UsePriority(WorkPriority.Critical))
            {
                FakeConnection connection = new(2, [1, 2]);
                var result = await downloads.DownloadAsync(new()
                {
                    Sources = ["mem://bad", "mem://good"],
                    DestinationPath = Path.Combine(root, "critical.bin"),
                    ConnectionFactory = source => source == "mem://bad" ? new FlakyConnection() : connection
                }).WaitAsync(TimeSpan.FromSeconds(10));
                AssertTrue(result.Success); AssertTrue(connection.WasStopped);
                AssertTrue(File.ReadAllBytes(Path.Combine(root, "critical.bin")) is [1, 2]);
                byte[] data = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray(); int segments = 0;
                result = await downloads.DownloadAsync(new()
                {
                    Sources = ["mem://segments"],
                    DestinationPath = Path.Combine(root, "segments.bin"),
                    MaxParallelSegments = 4,
                    ConnectionFactory = _ => new SchedulingSegmentedConnection(new(data), work, () => Interlocked.Increment(ref segments))
                }).WaitAsync(TimeSpan.FromSeconds(10));
                AssertTrue(result.Success); AssertTrue(segments > 1);
                AssertTrue(File.ReadAllBytes(Path.Combine(root, "segments.bin")).SequenceEqual(data));
            }
            AssertEqual(WorkPriority.Interactive, work.CurrentPriority);
            AssertTrue(work.Snapshot.Resources.All(r => r.Active == 0 && r.Waiting == 0));
            using var stop = new CancellationTokenSource();
            Task<DownloadTransferResult> optional;
            FakeConnection background = new(1, [3]);
            using (work.UsePriority(WorkPriority.Background)) optional = downloads.DownloadAsync(new()
            {
                Sources = ["mem://optional"],
                DestinationPath = Path.Combine(root, "optional.bin"),
                ConnectionFactory = _ => background
            }, cancellationToken: stop.Token);
            AssertFalse(optional.IsCompleted); AssertEqual(-1L, background.StartOffset);
            stop.Cancel();
            try { await optional; throw new InvalidOperationException("Paused download ignored cancellation."); }
            catch (OperationCanceledException) { }
            WaitForDrainedState(downloads);
            AssertTrue(work.Snapshot.Resources.All(r => r.Active == 0 && r.Waiting == 0));
            AssertFalse(File.Exists(Path.Combine(root, "optional.bin")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask ResourceIconsReleaseHttpBeforePausedDecode()
    {
        using WorkScheduler work = new(); IWorkQuietLease? quiet = null;
        int calls = 0;
        using HttpClient http = new(new ResourceHttp(_ =>
        {
            calls++; quiet = work.EnterQuiet();
            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII="))
            };
        }));
        using ResourceIconService icons = new(http) { WorkScheduler = work };
        Task<ResourceIconResult> image = icons.ReadAsync(new("https://cdn.modrinth.com/data/test/icon.png"), default);
        AssertFalse(image.IsCompleted); AssertEqual(1, calls);
        AssertEqual(0, work.Snapshot.Resources.Single(r => r.Resource == WorkResource.Http).Active);
        AssertEqual(1, work.Snapshot.Resources.Single(r => r.Resource == WorkResource.Cpu).Waiting);
        quiet!.Dispose(); AssertTrue((await image).Image is not null);
        using (work.EnterQuiet())
            AssertTrue((await icons.ReadAsync(new("https://cdn.modrinth.com/data/test/icon.png"), default)).Image is not null);
        AssertEqual(1, calls);
    }

    private sealed class SchedulingCatalogSource : IInstallCatalogSource
    {
        public int Calls;
        public Task<IReadOnlyList<InstallCatalogVersion>> GetGamesAsync(CancellationToken token) => GetLoadersAsync(InstallLoader.Fabric, "", token);
        public Task<IReadOnlyList<InstallCatalogVersion>> GetLoadersAsync(InstallLoader loader, string game, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Calls); return Task.FromResult<IReadOnlyList<InstallCatalogVersion>>([new("test", "")]); }
    }
    private sealed class SchedulingSegmentedConnection(FakeSegmentedConnection inner, WorkScheduler work, Action started) : ISegmentedDownloadConnection
    {
        public ValueTask<DownloadConnectionInfo> StartAsync(long beginOffset, CancellationToken token = default) => inner.StartAsync(beginOffset, token);
        public ValueTask<DownloadConnectionInfo> StartSegmentAsync(long beginOffset, long endOffset, CancellationToken token = default)
        {
            AssertEqual(1, work.Snapshot.Resources.Single(r => r.Resource == WorkResource.Http).Active);
            AssertEqual(beginOffset == 0 && endOffset == 0 ? 0 : 1,
                work.Snapshot.Resources.Single(r => r.Resource == WorkResource.Disk).Active);
            if (endOffset != 0) started();
            return inner.StartSegmentAsync(beginOffset, endOffset, token);
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public ValueTask StopAsync(CancellationToken token = default) => inner.StopAsync(token);
    }
    private sealed class SchedulingUpdateHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
    }
    private sealed class SchedulingClock : TimeProvider, IDisposable
    {
        private readonly List<SchedulingTimer> _timers = [];
        public int ActiveTimers => _timers.Count(t => !t.Disposed);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { SchedulingTimer timer = new(callback, state, dueTime); _timers.Add(timer); return timer; }
        public void Advance(TimeSpan elapsed) { foreach (SchedulingTimer timer in _timers.ToArray()) timer.Advance(elapsed); }
        public void Dispose() { foreach (SchedulingTimer timer in _timers) timer.Dispose(); }
        private sealed class SchedulingTimer(TimerCallback callback, object? state, TimeSpan remaining) : ITimer
        {
            public bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { remaining = dueTime; return !Disposed; }
            public void Advance(TimeSpan elapsed)
            { if (Disposed || remaining == Timeout.InfiniteTimeSpan) return; remaining -= elapsed; if (remaining <= TimeSpan.Zero) { remaining = Timeout.InfiniteTimeSpan; callback(state); } }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
