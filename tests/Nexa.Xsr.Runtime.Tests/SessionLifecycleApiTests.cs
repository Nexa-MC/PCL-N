using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static async ValueTask SessionHandshakeOwnsDeadlineAndTerminalCleanup()
    {
        foreach (string ending in new[] { "deadline", "cancel", "malformed", "correlation", "feature", "disconnect" })
        {
            var (hostStream, pluginStream) = SidecarLoopbackStream.CreatePair();
            using var host = new SidecarConnection(hostStream);
            using var plugin = new SidecarConnection(pluginStream);
            using var session = new SidecarHostSession(host, "Lifecycle", limits: new()
            { HandshakeTimeout = ending == "deadline" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(3) });
            using var cancellation = new CancellationTokenSource();
            Task handshake = session.HandshakeAsync(cancellation.Token).AsTask();
            SidecarFrame hello = await SessionReceiveAsync(plugin);
            AssertEqual(SidecarFeatures.All, SidecarHandshake.DecodeHelloDetails(hello.Payload.Span).Features);
            if (ending == "cancel") cancellation.Cancel();
            else if (ending == "disconnect") plugin.Dispose();
            else if (ending != "deadline")
            {
                byte[] payload = ending == "malformed" ? [255] : SidecarHandshake.EncodeWelcome(
                    SidecarProtocol.Version, Guid.NewGuid(), null,
                    ending == "feature" ? (SidecarFeatures)16 : SidecarFeatures.All);
                await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.Welcome,
                    SidecarFrameTraits.None, ending == "correlation" ? SidecarCorrelationId.Create() : hello.CorrelationId, payload));
            }
            await AssertThrowsAsync<SidecarProtocolException>(() => handshake.WaitAsync(TimeSpan.FromSeconds(3)));
            AssertEqual(SidecarSessionState.Failed, session.State);
            AssertTrue(host.State is SidecarConnectionState.Closed or SidecarConnectionState.Failed);
            AssertEqual(Guid.Empty, session.SessionId);
            AssertEqual(0, session.PendingCount);
            AssertTrue(session.Registration is null);
            AssertTrue(session.Mirror is null);
        }
    }

    private static async ValueTask SessionLifecycleSerializesInitialReaderPhases()
    {
        var (hostStream, pluginStream) = SidecarLoopbackStream.CreatePair();
        using var host = new SidecarConnection(hostStream);
        using var plugin = new SidecarConnection(pluginStream);
        using var session = new SidecarHostSession(host, "Lifecycle");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => session.HandshakeAsync(cancelled.Token).AsTask());
        AssertEqual(SidecarSessionState.Handshaking, session.State);
        Task first = session.HandshakeAsync().AsTask();
        Task duplicate = session.HandshakeAsync().AsTask();
        await AssertThrowsAsync<InvalidOperationException>(() => session.RunReceiveLoopAsync().AsTask());
        SidecarFrame hello = await SessionReceiveAsync(plugin);
        await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.Welcome, SidecarFrameTraits.None,
            hello.CorrelationId, SidecarHandshake.EncodeWelcome(SidecarProtocol.Version, Guid.NewGuid())));
        await first;
        await AssertThrowsAsync<InvalidOperationException>(() => duplicate);
        AssertEqual(SidecarSessionState.Registering, session.State);
        Task<SidecarStateMirror> registration = session.AcceptRegistrationAsync().AsTask();
        Task<SidecarStateMirror> duplicateRegistration = session.AcceptRegistrationAsync().AsTask();
        await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.RegisterBegin,
            SidecarFrameTraits.None, SidecarCorrelationId.Create(), SidecarRegistration.EncodeBegin(0)));
        await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.RegisterEnd,
            SidecarFrameTraits.Final, SidecarCorrelationId.Create(), ReadOnlyMemory<byte>.Empty));
        await registration;
        await AssertThrowsAsync<InvalidOperationException>(() => duplicateRegistration);
        Task snapshot = session.AcceptStateSnapshotAsync().AsTask();
        Task duplicateSnapshot = session.AcceptStateSnapshotAsync().AsTask();
        await SnapshotFrames(plugin, []);
        await snapshot;
        await AssertThrowsAsync<InvalidOperationException>(() => duplicateSnapshot);
        AssertEqual(SidecarMessageType.Ready, (await SessionReceiveAsync(plugin)).MessageType);
        AssertEqual(SidecarSessionState.Ready, session.State);
    }

    private static async ValueTask SessionRegistrationRequiresNegotiatedBinaryAndStreamFeatures()
    {
        SidecarRegistrationItem[] declarations =
        [
            new(SidecarRegistrationKind.Command, "plugin.binary.command", 0, SidecarValueCodecs.Bytes)
                { ResultCodecId = SidecarValueCodecs.Bool },
            new(SidecarRegistrationKind.Query, "plugin.binary.query", 0, SidecarValueCodecs.GeneratedDto)
                { ResultCodecId = SidecarValueCodecs.I64 },
            new(SidecarRegistrationKind.Event, "plugin.binary.event", 0, SidecarValueCodecs.F64),
            new(SidecarRegistrationKind.Stream, "plugin.binary.stream", 0, SidecarValueCodecs.Bytes)
                { ResultCodecId = SidecarValueCodecs.Bytes },
        ];
        foreach (SidecarFeatures features in new[] { SidecarFeatures.None, SidecarFeatures.BinaryPayloads, SidecarFeatures.Streams, SidecarFeatures.All })
        {
            var (hostStream, pluginStream) = SidecarLoopbackStream.CreatePair();
            using var host = new SidecarConnection(hostStream);
            using var plugin = new SidecarConnection(pluginStream);
            using var session = new SidecarHostSession(host, "Lifecycle");
            await CompleteLifecycleApiHandshake(session, plugin, features);
            AssertEqual(features, session.NegotiatedFeatures);
            Task<SidecarStateMirror> registration = session.AcceptRegistrationAsync().AsTask();
            SidecarRegistrationItem[] sent = features == SidecarFeatures.All ? declarations
                : [features == SidecarFeatures.None ? declarations[0] : declarations[3]];
            await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.RegisterBegin,
                SidecarFrameTraits.None, SidecarCorrelationId.Create(), SidecarRegistration.EncodeBegin((uint)sent.Length)));
            foreach (SidecarRegistrationItem declaration in sent)
                await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.RegisterItem,
                    SidecarFrameTraits.None, SidecarCorrelationId.Create(), SidecarRegistration.EncodeItem(declaration)));
            if (features is SidecarFeatures.None or SidecarFeatures.BinaryPayloads)
            {
                await AssertThrowsAsync<SidecarProtocolException>(() => registration);
                AssertEqual(SidecarSessionState.Failed, session.State);
                AssertTrue(session.Registration is null);
                continue;
            }
            await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.RegisterEnd,
                SidecarFrameTraits.Final, SidecarCorrelationId.Create(), ReadOnlyMemory<byte>.Empty));
            await registration;
            AssertEqual(features == SidecarFeatures.All ? 4 : 1, session.Registration!.Entries.Count);
            if (features == SidecarFeatures.All)
                AssertEqual(SidecarValueCodecs.I64,
                    session.Registration.TryResolveId(SidecarRegistrationKind.Query, 1)!.ResultCodecId);
            AssertEqual(SidecarValueCodecs.Bytes,
                session.Registration.TryResolveId(SidecarRegistrationKind.Stream, 1)!.ResultCodecId);
        }
    }

    private static async ValueTask SessionInitialSnapshotPublishesOneCoherentStore()
    {
        var (session, plugin, mirror, _) = await RegisteredTwoStates();
        using (session)
        using (plugin)
        {
            XsrStateStore placeholder = mirror.Store;
            XsrStateId count = mirror.TryResolve(XsrSemanticId_Parse("state.count"))!.Value;
            XsrStateId enabled = mirror.TryResolve(XsrSemanticId_Parse("state.enabled"))!.Value;
            int callbacks = 0;
            bool coherent = true;
            placeholder.Changed += _ =>
            {
                if (Volatile.Read(ref callbacks) < 2)
                    coherent &= placeholder.Read<int>(count).Value == 42
                        && placeholder.Read<bool>(enabled).Value
                        && placeholder.Read<int>(count).IsAvailable
                        && placeholder.Read<bool>(enabled).IsAvailable;
                Interlocked.Increment(ref callbacks);
            };
            Task snapshot = session.AcceptStateSnapshotAsync().AsTask();
            await SnapshotFrames(plugin, [(1u, BitConverter.GetBytes(42)), (2u, new byte[] { 1 })]);
            SidecarFrame ready = await SessionReceiveAsync(plugin);
            AssertEqual(SidecarMessageType.Ready, ready.MessageType);
            await snapshot;
            AssertTrue(ReferenceEquals(mirror, session.Mirror));
            AssertTrue(ReferenceEquals(placeholder, mirror.Store));
            AssertEqual(2, callbacks);
            AssertTrue(coherent);
            AssertEqual(42, mirror.Store.Read<int>(count).Value);
            AssertTrue(mirror.Store.Read<bool>(enabled).Value);
            AssertTrue(mirror.Store.Read<int>(count).IsAvailable);
            AssertTrue(mirror.Store.Read<bool>(enabled).IsAvailable);
            AssertEqual(1L, mirror.Store.Read<int>(count).Revision);
            AssertEqual(1L, mirror.Store.Read<bool>(enabled).Revision);
            await session.ActivateAsync();
            AssertEqual(SidecarMessageType.Activate, (await SessionReceiveAsync(plugin)).MessageType);
            Task loop = session.RunReceiveLoopAsync().AsTask();
            int beforeDelta = Volatile.Read(ref callbacks);
            await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.StateDelta,
                SidecarFrameTraits.None, SidecarCorrelationId.Create(),
                SidecarDataPlane.EncodeStateDelta(1, BitConverter.GetBytes(99))));
            await WaitUntil(() => placeholder.Read<int>(count).Value == 99 && Volatile.Read(ref callbacks) > beforeDelta);
            AssertTrue(ReferenceEquals(placeholder, mirror.Store));
            await session.ShutdownAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async ValueTask SessionDeactivationPausesDataPlaneAndSupportsReactivation()
    {
        var (session, plugin, mirror, loop) = await ActivatedSession();
        using (session)
        using (plugin)
        {
            List<string> events = [];
            session.AttachEventObserver(new RecordingDataEventObserver(events));
            XsrStateId progress = mirror.TryResolve(XsrSemanticId_Parse("plugin.download.progress"))!.Value;
            long sentBeforeCommand = session.ReadMetrics().SentFrames;
            Task<XsrResult> command = session.SendCommandAsync(XsrSemanticId_Parse("plugin.download.start")).AsTask();
            SidecarFrame request = await SessionReceiveAsync(plugin);
            // Deactivate while awaiting the result, after the request's write and flush finish.
            await WaitUntil(() => session.ReadMetrics().SentFrames > sentBeforeCommand);
            await session.DeactivateAsync();
            await ReceiveLifecycleApiMessage(plugin, SidecarMessageType.Deactivate);
            AssertEqual("xsr.unavailable", (await command).Error!.Code.Value);
            AssertEqual(0, session.PendingCount);
            AssertEqual(SidecarSessionState.Ready, session.State);
            AssertFalse(mirror.Store.Read<string>(progress).IsAvailable);
            long before = session.ReadMetrics().ReceivedFrames;
            await plugin.SendAsync(Delta("plugin.download.progress", "inactive"));
            await plugin.SendAsync(Event("plugin.download.completed", "inactive"));
            await WaitUntil(() => session.ReadMetrics().ReceivedFrames >= before + 2);
            AssertEqual("0", mirror.Store.Read<string>(progress).Value);
            AssertEqual(0, events.Count);
            AssertEqual("xsr.unavailable", (await session.SendQueryAsync(
                XsrSemanticId_Parse("plugin.download.status"))).Error!.Code.Value);
            await session.ActivateAsync();
            await ReceiveLifecycleApiMessage(plugin, SidecarMessageType.Activate);
            AssertTrue(mirror.Store.Read<string>(progress).IsAvailable);
            await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.CommandResult,
                SidecarFrameTraits.Final, request.CorrelationId, new byte[] { 255 }));
            Task<XsrResult<string>> query = session.SendQueryAsync(XsrSemanticId_Parse("plugin.download.status")).AsTask();
            SidecarFrame queryRequest = await ReceiveLifecycleApiMessage(plugin, SidecarMessageType.QueryRequest);
            await plugin.SendAsync(Result(queryRequest, true, "reactivated", null));
            AssertEqual("reactivated", (await query).Value);
            await plugin.SendAsync(Delta("plugin.download.progress", "active"));
            await WaitUntil(() => mirror.Store.Read<string>(progress).Value == "active");
            await session.ShutdownAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async ValueTask SessionGracefulShutdownPreservesOrderedWireControls()
    {
        var (session, plugin, loop) = await CreateLifecycleApiSession(SidecarFeatures.Unregistration);
        using (session)
        using (plugin)
        {
            Task shutdown = session.ShutdownAsync().AsTask();
            Task repeated = session.ShutdownAsync().AsTask();
            AssertEqual(SidecarMessageType.Deactivate, (await SessionReceiveAsync(plugin)).MessageType);
            SidecarFrame unregister = await SessionReceiveAsync(plugin);
            AssertEqual(SidecarMessageType.Unregister, unregister.MessageType);
            AssertEqual("Host shutdown.", SidecarControlMessages.DecodeUnregister(unregister.Payload.Span));
            AssertFalse(shutdown.IsCompleted);
            await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.Unregistered,
                SidecarFrameTraits.Final, SidecarCorrelationId.Create(), new byte[] { 255 }));
            await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.Unregistered,
                SidecarFrameTraits.Final, unregister.CorrelationId, SidecarControlMessages.EncodeUnregister("retired")));
            AssertEqual(SidecarMessageType.Shutdown, (await SessionReceiveAsync(plugin)).MessageType);
            await Task.WhenAll(shutdown, repeated, loop).WaitAsync(TimeSpan.FromSeconds(3));
            await session.ShutdownAsync();
            AssertEqual(SidecarSessionState.Closed, session.State);
            AssertEqual(0, session.PendingCount);
            AssertTrue(session.Registration is not null);
        }
    }

    private static async ValueTask SessionShutdownWithoutUnregisterReplyStillSendsShutdown()
    {
        var (session, plugin, loop) = await CreateLifecycleApiSession(SidecarFeatures.Unregistration,
            limits: new() { ShutdownTimeout = TimeSpan.FromMilliseconds(400) });
        using (session)
        using (plugin)
        {
            Task shutdown = session.ShutdownAsync().AsTask();
            AssertEqual(SidecarMessageType.Deactivate, (await SessionReceiveAsync(plugin)).MessageType);
            AssertEqual(SidecarMessageType.Unregister, (await SessionReceiveAsync(plugin)).MessageType);
            AssertEqual(SidecarMessageType.Shutdown, (await SessionReceiveAsync(plugin)).MessageType);
            await Task.WhenAll(shutdown, loop).WaitAsync(TimeSpan.FromSeconds(3));
            AssertEqual(SidecarSessionState.Closed, session.State);
        }
    }

    private static async ValueTask SessionExplicitAndPeerUnregistrationRetireTheSession()
    {
        foreach (bool peerInitiated in new[] { false, true })
        {
            var (session, plugin, loop) = await CreateLifecycleApiSession(SidecarFeatures.Unregistration);
            using (session)
            using (plugin)
            {
                if (peerInitiated)
                {
                    SidecarCorrelationId correlation = SidecarCorrelationId.Create();
                    await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.Unregister,
                        SidecarFrameTraits.None, correlation, SidecarControlMessages.EncodeUnregister("peer retired")));
                    SidecarFrame reply = await SessionReceiveAsync(plugin);
                    AssertEqual(SidecarMessageType.Unregistered, reply.MessageType);
                    AssertEqual(correlation, reply.CorrelationId);
                    AssertEqual("peer retired", SidecarControlMessages.DecodeUnregister(reply.Payload.Span));
                }
                else
                {
                    Task unregister = session.UnregisterAsync("host retired").AsTask();
                    AssertEqual(SidecarMessageType.Deactivate, (await SessionReceiveAsync(plugin)).MessageType);
                    SidecarFrame request = await SessionReceiveAsync(plugin);
                    AssertEqual(SidecarMessageType.Unregister, request.MessageType);
                    AssertEqual("host retired", SidecarControlMessages.DecodeUnregister(request.Payload.Span));
                    await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.Unregistered,
                        SidecarFrameTraits.Final, request.CorrelationId, SidecarControlMessages.EncodeUnregister("retired")));
                    await unregister;
                    await session.UnregisterAsync("already retired");
                }
                await loop.WaitAsync(TimeSpan.FromSeconds(3));
                AssertEqual(SidecarSessionState.Closed, session.State);
                XsrStateId progress = session.Mirror!.TryResolve(XsrSemanticId_Parse("plugin.download.progress"))!.Value;
                AssertFalse(session.Mirror.Store.Read<string>(progress).IsAvailable);
                AssertEqual("xsr.unavailable", (await session.SendCommandAsync(
                    XsrSemanticId_Parse("plugin.download.start"))).Error!.Code.Value);
            }
        }
        var (legacySession, legacyPlugin, _, legacyLoop) = await ActivatedSession();
        using (legacySession)
        using (legacyPlugin)
        {
            await AssertThrowsAsync<InvalidOperationException>(() => legacySession.UnregisterAsync().AsTask());
            AssertEqual(SidecarSessionState.Active, legacySession.State);
            await legacySession.ShutdownAsync();
            await legacyLoop.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async ValueTask CompleteLifecycleApiHandshake(
        SidecarHostSession session, SidecarConnection plugin, SidecarFeatures features)
    {
        Task handshake = session.HandshakeAsync().AsTask();
        SidecarFrame hello = await SessionReceiveAsync(plugin);
        await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.Welcome, SidecarFrameTraits.None,
            hello.CorrelationId, SidecarHandshake.EncodeWelcome(SidecarProtocol.Version, Guid.NewGuid(), null, features)));
        await handshake;
    }

    private static async ValueTask<(SidecarHostSession Session, SidecarConnection Plugin, Task Loop)> CreateLifecycleApiSession(
        SidecarFeatures features, SidecarSessionLimits? limits = null)
    {
        var (hostStream, pluginStream) = SidecarLoopbackStream.CreatePair();
        var plugin = new SidecarConnection(pluginStream);
        var session = new SidecarHostSession(new SidecarConnection(hostStream), "Lifecycle", limits: limits);
        await CompleteLifecycleApiHandshake(session, plugin, features);
        await RegisterOneState(session, plugin);
        await SnapshotAndReady(session, plugin);
        await session.ActivateAsync();
        AssertEqual(SidecarMessageType.Activate, (await SessionReceiveAsync(plugin)).MessageType);
        return (session, plugin, session.RunReceiveLoopAsync().AsTask());
    }

    private static async ValueTask<SidecarFrame> ReceiveLifecycleApiMessage(SidecarConnection plugin, SidecarMessageType expected)
    {
        while (true)
        {
            SidecarFrame frame = await SessionReceiveAsync(plugin);
            if (frame.MessageType == expected) return frame;
            AssertEqual(SidecarMessageType.Cancel, frame.MessageType);
        }
    }
}
