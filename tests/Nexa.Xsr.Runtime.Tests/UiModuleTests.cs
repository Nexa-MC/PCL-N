using System.Security.Cryptography;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr.State;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static readonly XsrSemanticId ModuleSlot = XsrSemanticId.Parse("ui.fixture.card");
    private static readonly XsrSemanticId ModuleState = XsrSemanticId.Parse("ui.fixture.modules");
    private static (XsrStateStore Store, XsrUiModuleRuntime Runtime, XsrStateId State) ModuleRuntime(IXsrStateObserver? observer = null)
    {
        XsrStateStoreBuilder builder = new(); builder.Cell<XsrUiModuleSnapshot>(ModuleState, "Fixture");
        var store = builder.Build(observer); var state = store.Resolve(ModuleState);
        return (store, new(store, state, ModuleSlot), state);
    }
    private static SidecarRegistrationItem ModuleItem(string id, string title)
    {
        var payload = new SidecarUiCard(ModuleSlot.Value, title, "Literal {body}").Encode();
        return new(SidecarRegistrationKind.UiModule, id, 0, 0, payload, SHA256.HashData(payload));
    }
    private static ValueTask<(SidecarHostSession Session, SidecarConnection Plugin)> ModuleSession(XsrUiModuleRuntime runtime,
        SidecarRegistrationItem[] items, Func<Stream, Stream>? wrap = null) => PatchSession(new(CaptionPoint), items,
            wrap: wrap, uiModules: new(runtime, ModuleSlot));

    private static async ValueTask ModulesPublishImmutableStateAndRestoreLivePredecessor()
    {
        var (store, runtime, state) = ModuleRuntime();
        var (first, peer) = await ModuleSession(runtime, [ModuleItem("module.first", "First")]);
        using (first) using (peer)
        {
            AssertTrue(store.Read<XsrUiModuleSnapshot>(state).Value is null);
            await SnapshotAll(first, peer); await first.ActivateAsync(); await DataPlaneReceiveAsync(peer);
            var prior = store.Read<XsrUiModuleSnapshot>(state).Value!;
            AssertEqual("First", prior.CardAt(runtime.Resolve(ModuleSlot))!.Title);
            var (second, secondPeer) = await ModuleSession(runtime, [ModuleItem("module.second", "Second")]);
            using (second) using (secondPeer)
            {
                await SnapshotAll(second, secondPeer); await second.ActivateAsync(); await DataPlaneReceiveAsync(secondPeer);
                AssertEqual("Second", store.Read<XsrUiModuleSnapshot>(state).Value!.CardAt(0)!.Title);
                AssertEqual("First", prior.CardAt(0)!.Title);
                await second.DeactivateAsync(); await DataPlaneReceiveAsync(secondPeer);
                AssertEqual("First", store.Read<XsrUiModuleSnapshot>(state).Value!.CardAt(0)!.Title);
                await second.ActivateAsync(); await DataPlaneReceiveAsync(secondPeer);
                AssertEqual("Second", store.Read<XsrUiModuleSnapshot>(state).Value!.CardAt(0)!.Title);
            }
            AssertEqual("First", store.Read<XsrUiModuleSnapshot>(state).Value!.CardAt(0)!.Title);
        }
        var empty = store.Read<XsrUiModuleSnapshot>(state).Value!;
        AssertTrue(empty.CardAt(0) is null);
        for (int i = 0; i < 100_000; i++) _ = empty.CardAt(0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) _ = empty.CardAt(0);
        AssertEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static async ValueTask ModulesRejectUnauthorizedMalformedAndOverBudgetBatches()
    {
        var valid = ModuleItem("module.valid", "Valid");
        List<SidecarRegistrationItem> invalid = [valid with { Flags = 1 }, valid with { CodecId = 1 }, valid with { RequiredResources = "not.granted" }];
        foreach (byte[] payload in new byte[][] { [], [1], new byte[4097], new SidecarUiCard("release.trust", "Title", "Body").Encode() })
            invalid.Add(valid with { Payload = payload, ContentHash = SHA256.HashData(payload) });
        using var duplicate = new SidecarPayloadWriter();
        duplicate.WriteUInt32(1, 1); duplicate.WriteString(2, ModuleSlot.Value); duplicate.WriteString(3, "Title"); duplicate.WriteString(4, "Body");
        var repeated = duplicate.ToArray(); repeated[^9] = 3; // Rewrite the fourth TLV's ID to duplicate field 3.
        invalid.Add(valid with { Payload = repeated, ContentHash = SHA256.HashData(repeated) });
        foreach (var item in invalid)
        {
            var (store, runtime, state) = ModuleRuntime();
            await AssertThrowsAsync<SidecarProtocolException>(() => ModuleSession(runtime,
                [valid, item with { SemanticId = "module.invalid" }]).AsTask());
            AssertTrue(store.Read<XsrUiModuleSnapshot>(state).Value is null);
        }
        var (budgetStore, budgetRuntime, budgetState) = ModuleRuntime();
        await AssertThrowsAsync<SidecarProtocolException>(() => ModuleSession(budgetRuntime,
            Enumerable.Range(0, 33).Select(i => ModuleItem("module.budget" + i, "Value")).ToArray()).AsTask());
        var (excess, peer) = await ModuleSession(budgetRuntime,
            Enumerable.Range(0, 9).Select(i => ModuleItem("module.limit" + i, "Value")).ToArray());
        using (excess) using (peer)
        {
            await SnapshotAll(excess, peer);
            await AssertThrowsAsync<SidecarProtocolException>(() => excess.ActivateAsync().AsTask());
            AssertEqual(SidecarSessionState.Failed, excess.State);
            AssertTrue(budgetStore.Read<XsrUiModuleSnapshot>(budgetState).Value is null);
        }
        using var writer = new SidecarPayloadWriter();
        writer.WriteUInt32(1, 1); writer.WriteString(2, ModuleSlot.Value); writer.WriteString(3, "Title"); writer.WriteString(4, "Body"); writer.WriteBoolean(5, true);
        AssertEqual("Body", SidecarUiCard.Decode(writer.ToArray()).Body);
        foreach (var card in new[] { new SidecarUiCard(ModuleSlot.Value, "\nTitle", "Body"), new(ModuleSlot.Value, "Title", new string('x', 513)), new(ModuleSlot.Value, "", "Body") })
            AssertThrows<SidecarProtocolException>(() => card.Encode());
    }

    private static async ValueTask ModulePublicationsRemainOrderedUnderReentrancyAndBlockedObservers()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int block = 1;
        var (store, runtime, state) = ModuleRuntime(new CaptionObserver(_ =>
        { if (Interlocked.Exchange(ref block, 0) == 0) return; entered.Set(); AssertTrue(release.Wait(TimeSpan.FromSeconds(5))); }));
        var (first, firstPeer) = await ModuleSession(runtime, [ModuleItem("module.first", "First")]);
        var (second, secondPeer) = await ModuleSession(runtime, [ModuleItem("module.second", "Second")]);
        using (first) using (firstPeer) using (second) using (secondPeer)
        {
            await SnapshotAll(first, firstPeer); await SnapshotAll(second, secondPeer);
            var activation = Task.Run(async () => await first.ActivateAsync());
            AssertTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            try { await second.ActivateAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); first.Dispose(); }
            finally { release.Set(); }
            await activation.WaitAsync(TimeSpan.FromSeconds(5));
            AssertEqual("Second", store.Read<XsrUiModuleSnapshot>(state).Value!.CardAt(0)!.Title);
            AssertEqual(SidecarSessionState.Closed, first.State);
        }
        SidecarHostSession? disposing = null;
        var (disposingStore, disposingRuntime, disposingState) = ModuleRuntime(new CaptionObserver(_ => disposing?.Dispose()));
        var (session, peer) = await ModuleSession(disposingRuntime, [ModuleItem("module.dispose", "Dispose")]);
        using (session) using (peer)
        {
            disposing = session;
            await SnapshotAll(session, peer); await session.ActivateAsync();
            AssertEqual(SidecarSessionState.Closed, session.State);
            AssertTrue(disposingStore.Read<XsrUiModuleSnapshot>(disposingState).Value!.CardAt(0) is null);
        }
    }

    private static async ValueTask ModuleTerminalPathsRetireAndBufferedActivationCannotResurrect()
    {
        foreach (var ending in new[] { SidecarMessageType.Shutdown, SidecarMessageType.Crash, SidecarMessageType.CommandResult })
        {
            var (store, runtime, state) = ModuleRuntime();
            var (session, peer) = await ModuleSession(runtime, [ModuleItem("module.terminal", "Title")]);
            using (session) using (peer)
            {
                await SnapshotAll(session, peer); await session.ActivateAsync(); await DataPlaneReceiveAsync(peer);
                var loop = session.RunReceiveLoopAsync().AsTask();
                await peer.SendAsync(new(SidecarProtocol.Version, ending, SidecarFrameTraits.Final,
                    SidecarCorrelationId.Create(), ending == SidecarMessageType.CommandResult ? new byte[] { 255 } : Array.Empty<byte>()));
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
                AssertTrue(store.Read<XsrUiModuleSnapshot>(state).Value!.CardAt(0) is null);
            }
        }
        var (raceStore, raceRuntime, raceState) = ModuleRuntime();
        BlockingPatchWrite? blocked = null;
        var (race, plugin) = await ModuleSession(raceRuntime, [ModuleItem("module.race", "Title")], inner => blocked = new(inner));
        using (race) using (plugin)
        {
            await SnapshotAll(race, plugin); blocked!.Blocked = true;
            Task activation = race.ActivateAsync().AsTask();
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            race.Dispose(); blocked.Release.TrySetResult();
            try { await activation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is IOException or InvalidOperationException or SidecarProtocolException) { }
            AssertTrue(raceStore.Read<XsrUiModuleSnapshot>(raceState).Value is null);
            AssertEqual(SidecarSessionState.Closed, race.State);
        }
    }

    private static async ValueTask ModuleActivationFailureDoesNotStrandEarlierCaptionPublication()
    {
        XsrStateStoreBuilder builder = new(); builder.Cell<XsrUiModuleSnapshot>(ModuleState, "Fixture"); builder.Cell<XsrUiPatchSnapshot>(CaptionState, "Fixture");
        var store = builder.Build(); var modules = new XsrUiModuleRuntime(store, store.Resolve(ModuleState), ModuleSlot);
        var captions = new XsrUiPatchRuntime(store, store.Resolve(CaptionState), new XsrUiCaptionTarget(CaptionTarget, 16));
        var items = Enumerable.Range(0, 9).Select(i => ModuleItem("module.limit" + i, "Title")).Append(CaptionItem("caption.failed", "Failed")).ToArray();
        var (failed, peer) = await PatchSession(new(CaptionPoint), items, uiPatches: new(captions, CaptionTarget), uiModules: new(modules, ModuleSlot));
        using (failed) using (peer)
        {
            await SnapshotAll(failed, peer); await AssertThrowsAsync<SidecarProtocolException>(() => failed.ActivateAsync().AsTask());
            AssertTrue(store.Read<XsrUiPatchSnapshot>(store.Resolve(CaptionState)).Value!.CaptionAt(0) is null);
        }
        var (next, nextPeer) = await CaptionSession(captions, [CaptionItem("caption.next", "Next")]);
        using (next) using (nextPeer)
        {
            await SnapshotAll(next, nextPeer); await next.ActivateAsync(); await DataPlaneReceiveAsync(nextPeer);
            AssertEqual("Next", store.Read<XsrUiPatchSnapshot>(store.Resolve(CaptionState)).Value!.CaptionAt(0));
        }
    }
}
