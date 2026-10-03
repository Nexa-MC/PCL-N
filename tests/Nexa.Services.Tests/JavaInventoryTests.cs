using Nexa.Services.Minecraft.Java;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask JavaInventoryRunsOnWorkerAndRefreshesWithoutStateWrites()
    {
        var java17 = Candidate("inventory-java17", new Version(17, 0), JavaBrand.Microsoft, false);
        var java21 = Candidate("inventory-java21", new Version(21, 0), JavaBrand.EclipseTemurin, true);
        var unavailable = java17 with { IsAvailable = false };
        var disabled = Candidate("inventory-disabled", new Version(8, 0), JavaBrand.OpenJdk, true) with { IsEnabled = false };
        var source = new List<JavaRuntimeCandidate> { unavailable, java17, disabled, java21 };
        using var locator = new InventoryGateLocator(source);
        var service = new JavaRuntimeInventoryService(locator);
        int caller = Environment.CurrentManagedThreadId;
        Task<Nexa.Xsr.XsrResult<JavaRuntimeInventorySnapshot>> pending = service.ReadAsync(new()).AsTask();
        AssertTrue(locator.Entered.Wait(TimeSpan.FromSeconds(5)));
        AssertTrue(locator.Worker != caller); AssertFalse(pending.IsCompleted); AssertEqual(0, locator.Invalidations);
        locator.Release.Set();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        AssertTrue(result.IsSuccess); AssertEqual(3, result.Value!.Runtimes.Count);
        AssertEqual(21, result.Value.Runtimes[0].Installation.MajorVersion);
        AssertTrue(result.Value.Runtimes[1].IsAvailable);
        AssertFalse(result.Value.Runtimes[2].IsEnabled);
        source.Clear(); AssertEqual(3, result.Value.Runtimes.Count);
        var router = new XsrQueryRouterBuilder();
        router.Register<JavaRuntimeInventoryQuery, JavaRuntimeInventorySnapshot>(JavaRuntimeInventoryContract.Query, service.ReadAsync);
        var queries = router.Build(new RecordingDispatchObserver());
        AssertTrue(queries.TryResolve(JavaRuntimeInventoryContract.Query, out var query));
        AssertTrue((await queries.QueryAsync<JavaRuntimeInventoryQuery, JavaRuntimeInventorySnapshot>(query, new(true))).IsSuccess);
        AssertEqual(1, locator.Invalidations);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.ReadAsync(new(true), cancelled.Token); throw new InvalidOperationException("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        AssertEqual(1, locator.Invalidations);
    }

    private static async ValueTask JavaInventoryRejectsLateNoncooperativeDiscovery()
    {
        using var locator = new InventoryGateLocator([]);
        using var stop = new CancellationTokenSource();
        Task<Nexa.Xsr.XsrResult<JavaRuntimeInventorySnapshot>> pending = new JavaRuntimeInventoryService(locator).ReadAsync(new(), stop.Token).AsTask();
        AssertTrue(locator.Entered.Wait(TimeSpan.FromSeconds(5))); stop.Cancel(); locator.Release.Set();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Late result accepted."); }
        catch (OperationCanceledException) { }
    }

    private sealed class InventoryGateLocator(IReadOnlyList<JavaRuntimeCandidate> candidates) : IJavaRuntimeLocator, IDisposable
    {
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        public int Worker, Invalidations;
        public void Invalidate() => Interlocked.Increment(ref Invalidations);
        public ValueTask<IReadOnlyList<JavaRuntimeCandidate>> FindAllAsync(CancellationToken cancellationToken = default)
        {
            Worker = Environment.CurrentManagedThreadId; Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5), CancellationToken.None)) throw new IOException("Fixture gate timed out.");
            return ValueTask.FromResult(candidates);
        }
        public ValueTask<JavaRuntimeCandidate?> InspectAsync(string javaExecutablePath, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<JavaRuntimeCandidate?>(null);
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
}
