using System.Security.Cryptography;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr.State;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static readonly XsrSemanticId CaptionTarget = XsrSemanticId.Parse("ui.fixture.caption");
    private static readonly XsrSemanticId CaptionState = XsrSemanticId.Parse("ui.fixture.captions");
    private static (XsrStateStore Store, XsrUiPatchRuntime Runtime, XsrStateId State) CaptionRuntime(IXsrStateObserver? observer = null)
    {
        XsrStateStoreBuilder builder = new(); builder.Cell<XsrUiPatchSnapshot>(CaptionState, "Fixture");
        var store = builder.Build(observer); var state = store.Resolve(CaptionState);
        return (store, new(store, state, new XsrUiCaptionTarget(CaptionTarget, 16)), state);
    }
    private static SidecarRegistrationItem CaptionItem(string id, string caption)
    {
        var payload = SidecarUiCaptionPatch.Encode(caption);
        return new(SidecarRegistrationKind.UiPatch, id, 0, 0, payload, SHA256.HashData(payload), TargetSemanticId: CaptionTarget.Value);
    }
    private static ValueTask<(SidecarHostSession Session, SidecarConnection Plugin)> CaptionSession(XsrUiPatchRuntime runtime,
        SidecarRegistrationItem[] items, Func<Stream, Stream>? wrap = null) => PatchSession(new(CaptionPoint), items,
            wrap: wrap, uiPatches: new(runtime, CaptionTarget));

    private static async ValueTask CaptionPatchesPublishImmutableStateAndRestorePriorActivations()
    {
        var (store, runtime, state) = CaptionRuntime();
        int index = runtime.Resolve(CaptionTarget);
        var (first, peer) = await CaptionSession(runtime, [CaptionItem("caption.first", "First")]);
        using (first) using (peer)
        {
            AssertTrue(store.Read<XsrUiPatchSnapshot>(state).Value is null);
            await SnapshotAll(first, peer); await first.ActivateAsync(); await DataPlaneReceiveAsync(peer);
            var prior = store.Read<XsrUiPatchSnapshot>(state).Value!;
            AssertEqual("First", prior.CaptionAt(index));
            var (second, secondPeer) = await CaptionSession(runtime, [CaptionItem("caption.second", "Second")]);
            using (second) using (secondPeer)
            {
                await SnapshotAll(second, secondPeer); await second.ActivateAsync(); await DataPlaneReceiveAsync(secondPeer);
                AssertEqual("Second", store.Read<XsrUiPatchSnapshot>(state).Value!.CaptionAt(index));
                AssertEqual("First", prior.CaptionAt(index));
                await second.DeactivateAsync(); await DataPlaneReceiveAsync(secondPeer);
                AssertEqual("First", store.Read<XsrUiPatchSnapshot>(state).Value!.CaptionAt(index));
                await second.ActivateAsync(); await DataPlaneReceiveAsync(secondPeer);
                AssertEqual("Second", store.Read<XsrUiPatchSnapshot>(state).Value!.CaptionAt(index));
            }
            AssertEqual("First", store.Read<XsrUiPatchSnapshot>(state).Value!.CaptionAt(index));
        }
        var empty = store.Read<XsrUiPatchSnapshot>(state).Value!;
        AssertTrue(empty.CaptionAt(index) is null);
        for (int i = 0; i < 100_000; i++) _ = empty.CaptionAt(index);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) _ = empty.CaptionAt(index);
        AssertEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static async ValueTask CaptionPatchesRejectGrantsBudgetsAndMalformedPayloadsAtomically()
    {
        var valid = CaptionItem("caption.valid", "Valid");
        List<SidecarRegistrationItem> invalid =
        [valid with { Flags = 1 }, valid with { CodecId = 1 }, valid with { TargetSemanticId = "release.trust" },
         CaptionItem("caption.bad", "A caption too long for this target")];
        foreach (byte[] payload in new byte[][] { [], [1], new byte[2049] })
            invalid.Add(valid with { Payload = payload, ContentHash = SHA256.HashData(payload) });
        foreach (var item in invalid)
        {
            var (store, runtime, state) = CaptionRuntime();
            await AssertThrowsAsync<SidecarProtocolException>(() => CaptionSession(runtime,
                [valid, item with { SemanticId = "caption.invalid" }]).AsTask());
            AssertTrue(store.Read<XsrUiPatchSnapshot>(state).Value is null);
        }
        var (budgetStore, budgetRuntime, budgetState) = CaptionRuntime();
        var (excess, peer) = await CaptionSession(budgetRuntime,
            Enumerable.Range(0, 33).Select(i => CaptionItem("caption.limit" + i, "Value")).ToArray());
        using (excess) using (peer)
        {
            await SnapshotAll(excess, peer);
            await AssertThrowsAsync<SidecarProtocolException>(() => excess.ActivateAsync().AsTask());
            AssertEqual(SidecarSessionState.Failed, excess.State);
            AssertTrue(budgetStore.Read<XsrUiPatchSnapshot>(budgetState).Value is null);
        }
        using var writer = new SidecarPayloadWriter();
        writer.WriteUInt32(1, 1); writer.WriteString(2, "Valid"); writer.WriteBoolean(3, true);
        AssertEqual("Valid", SidecarUiCaptionPatch.Decode(writer.ToArray())); // Unknown fields remain extensible.
        AssertThrows<SidecarProtocolException>(() => SidecarUiCaptionPatch.Encode("\nUnsafe"));
    }

    private static async ValueTask CaptionPublicationsCannotRegressWhenObserverBlocksOrDisposesSession()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int block = 1;
        var observer = new CaptionObserver(_ =>
        {
            if (Interlocked.Exchange(ref block, 0) == 0) return;
            entered.Set(); AssertTrue(release.Wait(TimeSpan.FromSeconds(5)));
        });
        var (store, runtime, state) = CaptionRuntime(observer);
        var (first, firstPeer) = await CaptionSession(runtime, [CaptionItem("caption.first", "First")]);
        var (second, secondPeer) = await CaptionSession(runtime, [CaptionItem("caption.second", "Second")]);
        using (first) using (firstPeer) using (second) using (secondPeer)
        {
            await SnapshotAll(first, firstPeer); await SnapshotAll(second, secondPeer);
            var activation = Task.Run(async () => await first.ActivateAsync());
            AssertTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                await second.ActivateAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                first.Dispose(); // Retires while the older publication is in its observer.
            }
            finally { release.Set(); }
            await activation.WaitAsync(TimeSpan.FromSeconds(5));
            AssertEqual("Second", store.Read<XsrUiPatchSnapshot>(state).Value!.CaptionAt(0));
            AssertEqual(SidecarSessionState.Closed, first.State);
        }
        AssertTrue(store.Read<XsrUiPatchSnapshot>(state).Value!.CaptionAt(0) is null);

        SidecarHostSession? disposing = null;
        var (disposingStore, disposingRuntime, disposingState) = CaptionRuntime(new CaptionObserver(_ => disposing?.Dispose()));
        var (session, peer) = await CaptionSession(disposingRuntime, [CaptionItem("caption.dispose", "Dispose")]);
        using (session) using (peer)
        {
            disposing = session;
            await SnapshotAll(session, peer); await session.ActivateAsync();
            AssertEqual(SidecarSessionState.Closed, session.State);
            AssertTrue(disposingStore.Read<XsrUiPatchSnapshot>(disposingState).Value!.CaptionAt(0) is null);
        }
    }

    private static async ValueTask CaptionTerminalPathsRestoreAndBufferedActivationCannotResurrect()
    {
        // CommandRequest exercises the invalid peer-to-Host direction.
        foreach (var ending in new[] { SidecarMessageType.Shutdown, SidecarMessageType.Crash, SidecarMessageType.CommandRequest })
        {
            var (store, runtime, state) = CaptionRuntime();
            var (session, peer) = await CaptionSession(runtime, [CaptionItem("caption.terminal", "Changed")]);
            using (session) using (peer)
            {
                await SnapshotAll(session, peer); await session.ActivateAsync(); await DataPlaneReceiveAsync(peer);
                var loop = session.RunReceiveLoopAsync().AsTask();
                await peer.SendAsync(new(SidecarProtocol.Version, ending, SidecarFrameTraits.Final,
                    SidecarCorrelationId.Create(), Array.Empty<byte>()));
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
                AssertTrue(store.Read<XsrUiPatchSnapshot>(state).Value!.CaptionAt(0) is null);
            }
        }
        var (raceStore, raceRuntime, raceState) = CaptionRuntime();
        BlockingPatchWrite? blocked = null;
        var (race, plugin) = await CaptionSession(raceRuntime, [CaptionItem("caption.race", "Changed")], inner => blocked = new(inner));
        using (race) using (plugin)
        {
            await SnapshotAll(race, plugin); blocked!.Blocked = true;
            Task activation = race.ActivateAsync().AsTask();
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            race.Dispose(); blocked.Release.TrySetResult();
            try { await activation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is IOException or InvalidOperationException or SidecarProtocolException) { }
            AssertTrue(raceStore.Read<XsrUiPatchSnapshot>(raceState).Value is null);
            AssertEqual(SidecarSessionState.Closed, race.State);
        }
    }

    private sealed class CaptionObserver(Action<XsrStateChange> changed) : IXsrStateObserver
    { public void OnChanged(XsrStateChange change) => changed(change); }
}
