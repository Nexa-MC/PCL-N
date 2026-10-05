using System.Buffers.Binary;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static async ValueTask SidecarBinaryDataApiUsesDeclaredNumericContracts()
    {
        await using var fixture = await DataApiSession([
            new(SidecarRegistrationKind.Command, "typed.command", 0, SidecarWireCodecs.I32) { ResultCodecId = SidecarWireCodecs.Bool },
            new(SidecarRegistrationKind.Query, "typed.query", 0, SidecarWireCodecs.Bool) { ResultCodecId = SidecarWireCodecs.I64 },
            new(SidecarRegistrationKind.Event, "typed.event", 0, SidecarWireCodecs.Bytes),
        ]);
        var command = fixture.Session.SendBinaryCommandByIdAsync(1, BinaryInt32(42)).AsTask();
        var request = await fixture.Peer.ReceiveAsync();
        var decoded = SidecarDataMessages.DecodeBinaryRequest(request.Payload.Span);
        AssertEqual(1u, decoded.ContractId); AssertEqual(42, BinaryPrimitives.ReadInt32LittleEndian(decoded.Value.Span));
        await DataApiFrame(fixture.Peer, SidecarMessageType.CommandResult,
            SidecarDataMessages.EncodeBinaryResult(true, new(SidecarWireCodecs.Bool, [1]), null), request.CorrelationId);
        AssertEqual(SidecarWireCodecs.Bool, (await command.WaitAsync(TimeSpan.FromSeconds(5))).Value.CodecId);

        var query = fixture.Session.SendBinaryQueryAsync(XsrSemanticId.Parse("typed.query"), new(SidecarWireCodecs.Bool, [1])).AsTask();
        request = await fixture.Peer.ReceiveAsync();
        AssertEqual(SidecarMessageType.QueryRequest, request.MessageType);
        byte[] integer = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(integer, -99);
        await DataApiFrame(fixture.Peer, SidecarMessageType.QueryResult,
            SidecarDataMessages.EncodeBinaryResult(true, new(SidecarWireCodecs.I64, integer), null), request.CorrelationId);
        AssertEqual(-99L, BinaryPrimitives.ReadInt64LittleEndian((await query.WaitAsync(TimeSpan.FromSeconds(5))).Value.Span));

        var events = new DataApiBinaryObserver(); fixture.Session.AttachBinaryEventObserver(events);
        await DataApiFrame(fixture.Peer, SidecarMessageType.Event, SidecarDataMessages.EncodeBinaryEvent(1, new(SidecarWireCodecs.Bytes, [9, 8])));
        var observed = await events.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AssertEqual("typed.event", observed.Semantic.Value); AssertTrue(observed.Value.Span.SequenceEqual(new byte[] { 9, 8 }));

        long sent = fixture.Session.ReadMetrics().SentFrames;
        var legacy = await fixture.Session.SendCommandAsync(XsrSemanticId.Parse("typed.command"));
        AssertEqual(XsrErrorKind.ContractMismatch, legacy.Error!.Kind);
        var mismatch = await fixture.Session.SendBinaryQueryByIdAsync(1, BinaryInt32(2));
        AssertEqual(XsrErrorKind.ContractMismatch, mismatch.Error!.Kind);
        AssertEqual(sent, fixture.Session.ReadMetrics().SentFrames);
        var metrics = fixture.Session.ReadMetrics();
        AssertEqual(2L, metrics.ExchangesStarted); AssertEqual(2L, metrics.ExchangesCompleted);
        AssertEqual(1L, metrics.EventsDelivered); AssertTrue(metrics.LastActivityUtc.HasValue);
    }

    private static async ValueTask SidecarDataApiRejectsUnavailableFeaturesAndPreCancellationLocally()
    {
        await using var fixture = await DataApiSession([new(SidecarRegistrationKind.Query, "legacy.query", 0, 0)], SidecarFeatures.None);
        long sent = fixture.Session.ReadMetrics().SentFrames;
        var binary = await fixture.Session.SendBinaryQueryByIdAsync(1);
        AssertEqual("xsr.feature_unavailable", binary.Error!.Code.Value);
        AssertEqual("xsr.feature_unavailable", (await fixture.Session.PingAsync()).Error!.Code.Value);
        AssertEqual("xsr.feature_unavailable", (await fixture.Session.OpenStreamByIdAsync(1)).Error!.Code.Value);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var result = await fixture.Session.SendQueryByIdAsync(1, cancellationToken: cancelled.Token);
        AssertEqual(XsrErrorKind.Cancelled, result.Error!.Kind);
        AssertEqual(XsrErrorKind.NotFound, (await fixture.Session.SendQueryByIdAsync(999)).Error!.Kind);
        // The original semantic signatures remain unambiguous for a default literal.
        AssertEqual(XsrErrorKind.NotFound, (await fixture.Session.SendQueryAsync(default)).Error!.Kind);
        AssertEqual(XsrErrorKind.NotFound, (await fixture.Session.SendCommandAsync(default)).Error!.Kind);
        AssertEqual(sent, fixture.Session.ReadMetrics().SentFrames); AssertEqual(0, fixture.Session.PendingCount);
        AssertEqual(XsrErrorKind.ContractMismatch,
            (await fixture.Session.SendQueryByIdAsync(1, timeout: TimeSpan.MaxValue)).Error!.Kind);
        // The legacy API still accepts a positive timeout beyond five minutes.
        var longer = fixture.Session.SendQueryByIdAsync(1, timeout: TimeSpan.FromHours(1)).AsTask();
        var request = await fixture.Peer.ReceiveAsync();
        await DataApiFrame(fixture.Peer, SidecarMessageType.QueryResult,
            SidecarDataMessages.EncodeResult(true, "longer timeout", null), request.CorrelationId);
        AssertEqual("longer timeout", (await longer.WaitAsync(TimeSpan.FromSeconds(5))).Value);
        fixture.Session.Dispose();
        AssertEqual(XsrErrorKind.Unavailable, (await fixture.Session.SendQueryByIdAsync(1)).Error!.Kind);
        AssertEqual(XsrErrorKind.Unavailable, (await fixture.Session.SendCommandByIdAsync(1)).Error!.Kind);
    }

    private static async ValueTask SidecarDataApiBindsResultMessageAndCodec()
    {
        foreach (bool wrongMessage in new[] { true, false })
        {
            await using var fixture = await DataApiSession([
                new(SidecarRegistrationKind.Query, "typed.query", 0, SidecarWireCodecs.I32) { ResultCodecId = SidecarWireCodecs.Bool },
            ]);
            var pending = fixture.Session.SendBinaryQueryByIdAsync(1, BinaryInt32(1)).AsTask();
            var request = await fixture.Peer.ReceiveAsync();
            await DataApiFrame(fixture.Peer, wrongMessage ? SidecarMessageType.CommandResult : SidecarMessageType.QueryResult,
                SidecarDataMessages.EncodeBinaryResult(true, wrongMessage ? new(SidecarWireCodecs.Bool, [1]) : BinaryInt32(9), null), request.CorrelationId);
            AssertEqual(XsrErrorKind.Unavailable, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Error!.Kind);
            await fixture.ReceiveLoop.WaitAsync(TimeSpan.FromSeconds(5));
            AssertEqual(SidecarSessionState.Failed, fixture.Session.State); AssertEqual(0, fixture.Session.PendingCount);
        }
    }

    private static async ValueTask SidecarDataApiIgnoresMalformedLateResultsAndNormalizesErrors()
    {
        await using var fixture = await DataApiSession([new(SidecarRegistrationKind.Query, "legacy.query", 0, 0)]);
        var timed = fixture.Session.SendQueryByIdAsync(1, timeout: TimeSpan.FromMilliseconds(100)).AsTask();
        var old = await fixture.Peer.ReceiveAsync();
        AssertEqual(XsrErrorKind.TimedOut, (await timed.WaitAsync(TimeSpan.FromSeconds(5))).Error!.Kind);
        AssertEqual(SidecarMessageType.Cancel, (await fixture.Peer.ReceiveAsync()).MessageType);
        var live = fixture.Session.SendQueryByIdAsync(1).AsTask();
        var current = await fixture.Peer.ReceiveAsync();
        await DataApiFrame(fixture.Peer, SidecarMessageType.QueryResult, [255], old.CorrelationId);
        await DataApiFrame(fixture.Peer, SidecarMessageType.QueryResult,
            SidecarDataMessages.EncodeResult(false, string.Empty, "invalid code\n"), current.CorrelationId);
        var rejected = await live.WaitAsync(TimeSpan.FromSeconds(5));
        AssertEqual(XsrErrorKind.Faulted, rejected.Error!.Kind); AssertEqual("xsr.handler_faulted", rejected.Error.Code.Value);
        AssertEqual(SidecarSessionState.Active, fixture.Session.State);
        AssertEqual(1L, fixture.Session.ReadMetrics().LateResults);
    }

    private static async ValueTask SidecarHealthApiCorrelatesNonceAndIgnoresLatePong()
    {
        await using var fixture = await DataApiSession([]);
        var ping = fixture.Session.PingAsync().AsTask();
        var request = await fixture.Peer.ReceiveAsync();
        AssertEqual(SidecarMessageType.HealthPing, request.MessageType);
        ulong nonce = SidecarControlMessages.DecodeHealth(request.Payload.Span);
        await DataApiFrame(fixture.Peer, SidecarMessageType.HealthPong, [255]);
        await DataApiFrame(fixture.Peer, SidecarMessageType.HealthPong, SidecarControlMessages.EncodeHealth(nonce), request.CorrelationId);
        AssertTrue((await ping.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        AssertTrue(fixture.Session.ReadMetrics().LastHealthRoundTrip.HasValue);
        var incoming = SidecarCorrelationId.Create();
        await DataApiFrame(fixture.Peer, SidecarMessageType.HealthPing, SidecarControlMessages.EncodeHealth(99), incoming);
        var response = await fixture.Peer.ReceiveAsync();
        AssertEqual(SidecarMessageType.HealthPong, response.MessageType); AssertEqual(incoming, response.CorrelationId);
        AssertEqual(99UL, SidecarControlMessages.DecodeHealth(response.Payload.Span));
        AssertEqual(0, fixture.Session.ReadMetrics().PendingHealthChecks);
    }

    private static async ValueTask SidecarHealthApiTimesOutAndStopsPendingOnDeactivation()
    {
        await using var fixture = await DataApiSession([]);
        var timeout = fixture.Session.PingAsync(TimeSpan.FromMilliseconds(100)).AsTask();
        await fixture.Peer.ReceiveAsync();
        AssertEqual(XsrErrorKind.TimedOut, (await timeout.WaitAsync(TimeSpan.FromSeconds(5))).Error!.Kind);
        long sentBeforePing = fixture.Session.ReadMetrics().SentFrames;
        var ping = fixture.Session.PingAsync().AsTask(); await fixture.Peer.ReceiveAsync();
        // The pending pong is cancelled after the request's write and flush have completed.
        await WaitUntil(() => fixture.Session.ReadMetrics().SentFrames > sentBeforePing);
        await fixture.Session.DeactivateAsync();
        AssertEqual(XsrErrorKind.Unavailable, (await ping.WaitAsync(TimeSpan.FromSeconds(5))).Error!.Kind);
        AssertEqual(SidecarMessageType.Deactivate, (await fixture.Peer.ReceiveAsync()).MessageType);
        await fixture.Session.ActivateAsync(); await fixture.Peer.ReceiveAsync();
        ping = fixture.Session.PingAsync().AsTask(); var request = await fixture.Peer.ReceiveAsync();
        await DataApiFrame(fixture.Peer, SidecarMessageType.HealthPong, request.Payload.ToArray(), request.CorrelationId);
        AssertTrue((await ping.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    private static async ValueTask SidecarHealthApiRejectsMismatchedNonceAndRetiresAllChecks()
    {
        await using var fixture = await DataApiSession([]);
        var first = fixture.Session.PingAsync().AsTask();
        var request = await fixture.Peer.ReceiveAsync();
        var second = fixture.Session.PingAsync().AsTask(); await fixture.Peer.ReceiveAsync();
        ulong nonce = SidecarControlMessages.DecodeHealth(request.Payload.Span);
        await DataApiFrame(fixture.Peer, SidecarMessageType.HealthPong, SidecarControlMessages.EncodeHealth(nonce + 1), request.CorrelationId);
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        AssertTrue(results.All(result => result.Error?.Kind == XsrErrorKind.Unavailable));
        await fixture.ReceiveLoop.WaitAsync(TimeSpan.FromSeconds(5));
        AssertEqual(SidecarSessionState.Failed, fixture.Session.State); AssertEqual(0, fixture.Session.ReadMetrics().PendingHealthChecks);
    }

    private static async ValueTask SidecarRemoteErrorCompletesOnlyItsCorrelatedExchange()
    {
        await using var fixture = await DataApiSession([new(SidecarRegistrationKind.Query, "legacy.query", 0, 0)]);
        var first = fixture.Session.SendQueryByIdAsync(1).AsTask(); var request = await fixture.Peer.ReceiveAsync();
        var second = fixture.Session.SendQueryByIdAsync(1).AsTask(); var secondRequest = await fixture.Peer.ReceiveAsync();
        await DataApiFrame(fixture.Peer, SidecarMessageType.Error,
            SidecarControlMessages.EncodeFailure("plugin.rejected", "A safe diagnostic."), request.CorrelationId);
        AssertEqual("plugin.rejected", (await first.WaitAsync(TimeSpan.FromSeconds(5))).Error!.Code.Value);
        AssertEqual(1, fixture.Session.PendingCount); AssertFalse(second.IsCompleted);
        AssertEqual("plugin.rejected", fixture.Session.ReadMetrics().LastRemoteError);
        await DataApiFrame(fixture.Peer, SidecarMessageType.QueryResult,
            SidecarDataMessages.EncodeResult(true, "ok", null), secondRequest.CorrelationId);
        AssertEqual("ok", (await second.WaitAsync(TimeSpan.FromSeconds(5))).Value);
        AssertEqual(SidecarSessionState.Active, fixture.Session.State);
    }

    private static async ValueTask SidecarLegacySessionRejectsBinaryCodecZeroRepresentation()
    {
        await using var fixture = await DataApiSession([new(SidecarRegistrationKind.Query, "legacy.query", 0, 0)], SidecarFeatures.None);
        var query = fixture.Session.SendQueryByIdAsync(1).AsTask(); var request = await fixture.Peer.ReceiveAsync();
        await DataApiFrame(fixture.Peer, SidecarMessageType.QueryResult,
            SidecarDataMessages.EncodeBinaryResult(true, new(SidecarWireCodecs.Utf8String, "ok"u8), null), request.CorrelationId);
        AssertEqual(XsrErrorKind.Unavailable, (await query.WaitAsync(TimeSpan.FromSeconds(5))).Error!.Kind);
        await fixture.ReceiveLoop.WaitAsync(TimeSpan.FromSeconds(5)); AssertEqual(SidecarSessionState.Failed, fixture.Session.State);
    }

    private static async ValueTask<DataApiFixture> DataApiSession(SidecarRegistrationItem[] items,
        SidecarFeatures features = SidecarFeatures.All, Func<Stream, Stream>? wrap = null)
    {
        var (host, peer) = SidecarLoopbackStream.CreatePair();
        var connection = new SidecarConnection(peer);
        var session = new SidecarHostSession(new(wrap?.Invoke(host) ?? host), "DataApiFixture");
        var handshake = session.HandshakeAsync().AsTask(); var hello = await connection.ReceiveAsync();
        await DataApiFrame(connection, SidecarMessageType.Welcome,
            SidecarHandshake.EncodeWelcome(SidecarProtocol.Version, Guid.NewGuid(), null, features), hello.CorrelationId);
        await handshake;
        var registration = session.AcceptRegistrationAsync().AsTask();
        await DataApiFrame(connection, SidecarMessageType.RegisterBegin, SidecarRegistration.EncodeBegin((uint)items.Length));
        foreach (var item in items) await DataApiFrame(connection, SidecarMessageType.RegisterItem, SidecarRegistration.EncodeItem(item));
        await DataApiFrame(connection, SidecarMessageType.RegisterEnd, []); await registration;
        await SnapshotAll(session, connection);
        await session.ActivateAsync(); await connection.ReceiveAsync();
        return new(session, connection, session.RunReceiveLoopAsync().AsTask());
    }

    private static ValueTask DataApiFrame(SidecarConnection peer, SidecarMessageType type, byte[] payload,
        SidecarCorrelationId correlation = default) => peer.SendAsync(new(SidecarProtocol.Version, type, SidecarFrameTraits.None,
            correlation.IsAssigned ? correlation : SidecarCorrelationId.Create(), payload));

    private static SidecarBinaryValue BinaryInt32(int value)
    { byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); return new(SidecarWireCodecs.I32, bytes); }

    private sealed class DataApiBinaryObserver : ISidecarSessionBinaryEventObserver
    {
        internal TaskCompletionSource<(XsrSemanticId Semantic, SidecarBinaryValue Value)> Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnEvent(XsrSemanticId semanticId, SidecarBinaryValue value) => Observed.TrySetResult((semanticId, value));
    }

    private sealed class DataApiFixture(SidecarHostSession session, SidecarConnection peer, Task receiveLoop) : IAsyncDisposable
    {
        internal SidecarHostSession Session { get; } = session;
        internal SidecarConnection Peer { get; } = peer;
        internal Task ReceiveLoop { get; } = receiveLoop;
        public async ValueTask DisposeAsync()
        { Session.Dispose(); Peer.Dispose(); await ReceiveLoop.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
