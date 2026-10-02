using System.Security.Cryptography;
using System.Text;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static readonly XsrSemanticId SignalIntent = XsrSemanticId.Parse("ui.fixture.search");
    private static readonly XsrSemanticId SignalEvent = XsrSemanticId.Parse("event.fixture.done");
    private static XsrSignalRuntime SignalRuntime() => new(new(SignalIntent, XsrSignalKind.Intent, true), new(SignalEvent, XsrSignalKind.Event));
    private static SidecarRegistrationItem SignalItem(string id, SidecarRegistrationKind kind, XsrSemanticId target,
        bool consume = false, string match = "")
    {
        byte[] payload = [.. "NXS1"u8.ToArray(), consume ? (byte)1 : (byte)0, .. Encoding.UTF8.GetBytes(match)];
        return new(kind, id, 0, 0, payload, SHA256.HashData(payload), TargetSemanticId: target.Value);
    }
    private static ValueTask<(SidecarHostSession Session, SidecarConnection Plugin)> SignalSession(XsrSignalRuntime runtime,
        SidecarRegistrationItem[] items, Func<Stream, Stream>? wrap = null) =>
        PatchSession(new(CaptionPoint), items, wrap: wrap, signals: new(runtime, SignalIntent, SignalEvent));
    private static async Task<SidecarHookSignal> ReceiveSignal(SidecarConnection peer)
    {
        var frame = await DataPlaneReceiveAsync(peer);
        AssertEqual(SidecarMessageType.HookSignal, frame.MessageType);
        return SidecarHookSignal.Decode(frame.Payload.Span);
    }

    private static async ValueTask SignalsExecuteLocallyAndNotifyInOrder()
    {
        var runtime = SignalRuntime();
        var intent = runtime.Resolve(SignalIntent);
        var done = runtime.Resolve(SignalEvent);
        var (session, peer) = await SignalSession(runtime,
            [SignalItem("signal.catch", SidecarRegistrationKind.IntentCatch, SignalIntent, true, "search"),
             SignalItem("signal.wait", SidecarRegistrationKind.IntentWait, SignalIntent),
             SignalItem("signal.listen", SidecarRegistrationKind.EventListen, SignalEvent)]);
        using (session) using (peer)
        {
            AssertFalse(runtime.Emit(intent, "search"));
            await SnapshotAll(session, peer); await session.ActivateAsync(); await DataPlaneReceiveAsync(peer);
            AssertTrue(runtime.Emit(intent, "search"));
            AssertFalse(runtime.Emit(done, "success"));
            var first = await ReceiveSignal(peer);
            var second = await ReceiveSignal(peer);
            var third = await ReceiveSignal(peer);
            AssertEqual(SidecarRegistrationKind.IntentCatch, first.Kind);
            AssertEqual(SidecarRegistrationKind.IntentWait, second.Kind);
            AssertEqual(SidecarRegistrationKind.EventListen, third.Kind);
            AssertEqual("search", first.Value); AssertEqual("success", third.Value);
            AssertEqual(first.ActivationId, third.ActivationId);
            AssertEqual(1UL, first.Sequence); AssertEqual(2UL, second.Sequence); AssertEqual(3UL, third.Sequence);
            AssertFalse(runtime.Emit(intent, "other")); // Wait is already fulfilled; catch does not match.
            runtime.Emit(done, "failure");
            AssertEqual(4UL, (await ReceiveSignal(peer)).Sequence);
            await session.DeactivateAsync(); await DataPlaneReceiveAsync(peer);
            AssertFalse(runtime.Emit(intent, "search"));
            await session.ActivateAsync(); await DataPlaneReceiveAsync(peer);
            runtime.Emit(intent, "other");
            var reactivated = await ReceiveSignal(peer);
            AssertEqual(SidecarRegistrationKind.IntentWait, reactivated.Kind);
            AssertTrue(first.ActivationId != reactivated.ActivationId);
            AssertEqual(1UL, reactivated.Sequence);
        }
        AssertFalse(runtime.Emit(intent, "search"));
    }

    private static async ValueTask SignalsRejectUnauthorizedOrMalformedRegistrationAtomically()
    {
        var valid = SignalItem("signal.valid", SidecarRegistrationKind.IntentCatch, SignalIntent, true);
        byte[][] malformed = [[1], [.. "NXS1"u8.ToArray(), 2], [.. "NXS1"u8.ToArray(), 0, 255],
            [.. "NXS1"u8.ToArray(), 0, .. new byte[513]]];
        List<SidecarRegistrationItem> rejected =
        [valid with { TargetSemanticId = "account.ownership" }, valid with { Flags = 1 }, valid with { CodecId = 1 },
         valid with { Kind = SidecarRegistrationKind.EventCatch },
         SignalItem("signal.bad", SidecarRegistrationKind.EventCatch, SignalEvent, true),
         SignalItem("signal.bad", SidecarRegistrationKind.IntentWait, SignalIntent, true)];
        rejected.AddRange(malformed.Select(payload => valid with { Payload = payload, ContentHash = SHA256.HashData(payload) }));
        foreach (var item in rejected)
        {
            var runtime = SignalRuntime();
            await AssertThrowsAsync<SidecarProtocolException>(() => SignalSession(runtime,
                [valid, item with { SemanticId = "signal.bad" }]).AsTask());
            AssertFalse(runtime.Emit(runtime.Resolve(SignalIntent), "value"));
        }
        var own = SignalRuntime();
        AssertThrows<ArgumentException>(() => own.Emit(SignalRuntime().Resolve(SignalIntent), "value"));
    }

    private static async ValueTask SignalOverflowDisablesCatchAndFailsSessionWithoutBlockingCaller()
    {
        var runtime = SignalRuntime();
        var point = runtime.Resolve(SignalIntent);
        BlockingPatchWrite? stream = null;
        var (session, peer) = await SignalSession(runtime,
            [SignalItem("signal.catch", SidecarRegistrationKind.IntentCatch, SignalIntent, true)], inner => stream = new(inner));
        using (session) using (peer)
        {
            await SnapshotAll(session, peer); await session.ActivateAsync(); await DataPlaneReceiveAsync(peer);
            stream!.Blocked = true;
            AssertTrue(runtime.Emit(point, "first"));
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 128; i++) AssertTrue(runtime.Emit(point, "queued"));
            AssertFalse(runtime.Emit(point, "overflow"));
            AssertFalse(runtime.Emit(point, "disabled"));
            stream.Release.TrySetResult();
            await WaitUntil(() => session.State == SidecarSessionState.Failed);
            AssertFalse(runtime.Emit(point, "retired"));
        }
    }

    private static async ValueTask SignalTerminalPathsRetireAndBufferedActivationCannotResurrect()
    {
        foreach (var ending in new[] { SidecarMessageType.Shutdown, SidecarMessageType.Crash, SidecarMessageType.CommandResult })
        {
            var runtime = SignalRuntime();
            var point = runtime.Resolve(SignalIntent);
            var (session, peer) = await SignalSession(runtime,
                [SignalItem("signal.catch", SidecarRegistrationKind.IntentCatch, SignalIntent, true)]);
            using (session) using (peer)
            {
                await SnapshotAll(session, peer); await session.ActivateAsync(); await DataPlaneReceiveAsync(peer);
                AssertTrue(runtime.Emit(point, "before")); await ReceiveSignal(peer);
                var loop = session.RunReceiveLoopAsync().AsTask();
                await peer.SendAsync(new(SidecarProtocol.Version, ending, SidecarFrameTraits.Final,
                    SidecarCorrelationId.Create(), ending == SidecarMessageType.CommandResult ? new byte[] { 255 } : Array.Empty<byte>()));
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
                AssertFalse(runtime.Emit(point, "after"));
            }
        }
        var raceRuntime = SignalRuntime();
        BlockingPatchWrite? blocked = null;
        var (race, plugin) = await SignalSession(raceRuntime,
            [SignalItem("signal.catch", SidecarRegistrationKind.IntentCatch, SignalIntent, true)], inner => blocked = new(inner));
        using (race) using (plugin)
        {
            await SnapshotAll(race, plugin);
            blocked!.Blocked = true;
            Task activation = race.ActivateAsync().AsTask();
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            race.Dispose(); blocked.Release.TrySetResult();
            try { await activation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is IOException or InvalidOperationException or SidecarProtocolException) { }
            AssertFalse(raceRuntime.Emit(raceRuntime.Resolve(SignalIntent), "retired"));
            AssertEqual(SidecarSessionState.Closed, race.State);
        }
    }

    private static void InactiveSignalsAllocateNothingAndWireRejectsMissingIdentity()
    {
        var runtime = SignalRuntime(); var point = runtime.Resolve(SignalIntent);
        for (int i = 0; i < 100_000; i++) runtime.Emit(point, "value");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) runtime.Emit(point, "value");
        AssertEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        AssertThrows<SidecarProtocolException>(() => SidecarHookSignal.Decode([]));
        AssertThrows<SidecarProtocolException>(() => new SidecarHookSignal(Guid.Empty,
            SidecarRegistrationKind.IntentCatch, 1, 1, "value").Encode());
    }

    private static async ValueTask SignalBudgetsAreAtomicAndConcurrentWaitDeliversOnce()
    {
        var runtime = SignalRuntime();
        var items = Enumerable.Range(0, 129).Select(i => SignalItem("signal.limit" + i,
            SidecarRegistrationKind.IntentWait, SignalIntent)).ToArray();
        await AssertThrowsAsync<SidecarProtocolException>(() => SignalSession(runtime, items).AsTask());
        var (excess, excessPeer) = await SignalSession(runtime, items[..33]);
        using (excess) using (excessPeer)
        {
            await SnapshotAll(excess, excessPeer);
            await AssertThrowsAsync<SidecarProtocolException>(() => excess.ActivateAsync().AsTask());
            AssertEqual(SidecarSessionState.Failed, excess.State);
        }
        var (session, peer) = await SignalSession(runtime,
            [SignalItem("signal.wait", SidecarRegistrationKind.IntentWait, SignalIntent),
             SignalItem("signal.listen", SidecarRegistrationKind.EventListen, SignalEvent)]);
        using (session) using (peer)
        {
            await SnapshotAll(session, peer); await session.ActivateAsync(); await DataPlaneReceiveAsync(peer);
            var point = runtime.Resolve(SignalIntent);
            Parallel.For(0, 64, _ => runtime.Emit(point, "value"));
            runtime.Emit(runtime.Resolve(SignalEvent), "after");
            var wait = await ReceiveSignal(peer); var after = await ReceiveSignal(peer);
            AssertEqual(SidecarRegistrationKind.IntentWait, wait.Kind);
            AssertEqual(SidecarRegistrationKind.EventListen, after.Kind);
            AssertEqual(1UL, wait.Sequence); AssertEqual(2UL, after.Sequence);
        }
    }
}
