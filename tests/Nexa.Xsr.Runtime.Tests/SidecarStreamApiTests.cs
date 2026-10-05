using Nexa.Sidecar.Protocol;
using Nexa.Xsr;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static SidecarRegistrationItem StreamApiDeclaration() =>
        new(SidecarRegistrationKind.Stream, "typed.stream", 0, 0) { ResultCodecId = SidecarWireCodecs.Bytes };

    private static async ValueTask SidecarStreamsReturnCreditAndDrainInOrder()
    {
        await using var fixture = await DataApiSession([StreamApiDeclaration()]);
        var opened = await fixture.Session.OpenStreamByIdAsync(1); AssertTrue(opened.IsSuccess);
        await using var stream = opened.Value;
        var request = await fixture.Peer.ReceiveAsync();
        AssertEqual(SidecarMessageType.StreamOpen, request.MessageType);
        var open = SidecarStreamMessages.DecodeOpen(request.Payload.Span);
        AssertEqual(16u, open.InitialCredit); AssertEqual(1u, open.ContractId);
        await using var reader = stream.ReadAllAsync().GetAsyncEnumerator();
        await DataApiFrame(fixture.Peer, SidecarMessageType.StreamChunk,
            SidecarStreamMessages.EncodeChunk(1, new(SidecarWireCodecs.Bytes, [1, 2])), request.CorrelationId);
        AssertTrue(await reader.MoveNextAsync()); AssertTrue(reader.Current.Span.SequenceEqual(new byte[] { 1, 2 }));
        var credit = await fixture.Peer.ReceiveAsync();
        AssertEqual(SidecarMessageType.StreamCredit, credit.MessageType); AssertEqual(request.CorrelationId, credit.CorrelationId);
        AssertEqual(1u, SidecarStreamMessages.DecodeCredit(credit.Payload.Span));
        long received = fixture.Session.ReadMetrics().ReceivedFrames;
        await DataApiFrame(fixture.Peer, SidecarMessageType.StreamChunk,
            SidecarStreamMessages.EncodeChunk(2, new(SidecarWireCodecs.Bytes, [3])), request.CorrelationId);
        await DataApiFrame(fixture.Peer, SidecarMessageType.StreamEnd,
            SidecarStreamMessages.EncodeEnd(3, true, null), request.CorrelationId);
        await WaitUntil(() => fixture.Session.ReadMetrics().ReceivedFrames >= received + 2 && stream.Completion.IsCompleted);
        AssertTrue((await stream.Completion).IsSuccess);
        long sent = fixture.Session.ReadMetrics().SentFrames;
        AssertTrue(await reader.MoveNextAsync()); AssertEqual((byte)3, reader.Current.Span[0]);
        AssertFalse(await reader.MoveNextAsync());
        AssertEqual(sent, fixture.Session.ReadMetrics().SentFrames);
        AssertEqual(0L, fixture.Session.ReadMetrics().BufferedStreamBytes); AssertEqual(0, fixture.Session.ReadMetrics().OpenStreams);
        AssertEqual(2L, fixture.Session.ReadMetrics().StreamChunks);
    }

    private static async ValueTask SidecarStreamsCanFinishBeforeTheOpenFlushCompletes()
    {
        StreamOpenFlushBarrier? barrier = null;
        await using var fixture = await DataApiSession([StreamApiDeclaration()], wrap: inner => barrier = new(inner));
        barrier!.Block = true;
        var opened = fixture.Session.OpenStreamByIdAsync(1).AsTask();
        try
        {
            var request = await fixture.Peer.ReceiveAsync();
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            long received = fixture.Session.ReadMetrics().ReceivedFrames;
            await DataApiFrame(fixture.Peer, SidecarMessageType.StreamEnd, SidecarStreamMessages.EncodeEnd(1, true, null), request.CorrelationId);
            await WaitUntil(() => fixture.Session.ReadMetrics().ReceivedFrames >= received + 1 && fixture.Session.ReadMetrics().OpenStreams == 0);
            AssertFalse(opened.IsCompleted);
        }
        finally { barrier.Release.TrySetResult(); }
        var result = await opened.WaitAsync(TimeSpan.FromSeconds(5)); AssertTrue(result.IsSuccess);
        await using var stream = result.Value; AssertTrue((await stream.Completion).IsSuccess);
        await using var reader = stream.ReadAllAsync().GetAsyncEnumerator(); AssertFalse(await reader.MoveNextAsync());
        AssertEqual(SidecarSessionState.Active, fixture.Session.State);
    }

    private static async ValueTask SidecarCompletedStreamsPreserveUnreadChunksAcrossSessionRetirement()
    {
        foreach (string ending in new[] { "deactivate", "dispose", "crash" })
        {
            await using var fixture = await DataApiSession([StreamApiDeclaration()]);
            await using var completed = (await fixture.Session.OpenStreamByIdAsync(1)).Value;
            var request = await fixture.Peer.ReceiveAsync();
            await DataApiFrame(fixture.Peer, SidecarMessageType.StreamChunk,
                SidecarStreamMessages.EncodeChunk(1, new(SidecarWireCodecs.Bytes, [1, 2])), request.CorrelationId);
            await DataApiFrame(fixture.Peer, SidecarMessageType.StreamChunk,
                SidecarStreamMessages.EncodeChunk(2, new(SidecarWireCodecs.Bytes, [3, 4])), request.CorrelationId);
            await DataApiFrame(fixture.Peer, SidecarMessageType.StreamEnd,
                SidecarStreamMessages.EncodeEnd(3, true, null), request.CorrelationId);
            AssertTrue((await completed.Completion.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            AssertEqual(4L, fixture.Session.ReadMetrics().BufferedStreamBytes);

            SidecarHostStream? newer = null;
            if (ending == "deactivate")
            {
                await fixture.Session.DeactivateAsync(); await fixture.Peer.ReceiveAsync();
                AssertEqual(0, fixture.Session.ReadMetrics().OpenStreams); AssertEqual(0L, fixture.Session.ReadMetrics().BufferedStreamBytes);
                await fixture.Session.ActivateAsync(); await fixture.Peer.ReceiveAsync();
                newer = (await fixture.Session.OpenStreamByIdAsync(1)).Value;
                var nextRequest = await fixture.Peer.ReceiveAsync();
                await DataApiFrame(fixture.Peer, SidecarMessageType.StreamChunk,
                    SidecarStreamMessages.EncodeChunk(1, new(SidecarWireCodecs.Bytes, [9, 8, 7])), nextRequest.CorrelationId);
                await WaitUntil(() => fixture.Session.ReadMetrics().BufferedStreamBytes == 3);
            }
            else if (ending == "dispose") fixture.Session.Dispose();
            else
            {
                await DataApiFrame(fixture.Peer, SidecarMessageType.Crash,
                    SidecarControlMessages.EncodeFailure("plugin.crashed", "Fixture stopped."));
                await fixture.ReceiveLoop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            try
            {
                var values = new List<byte[]>();
                await foreach (var value in completed.ReadAllAsync()) values.Add(value.ToArray());
                AssertEqual(2, values.Count);
                AssertTrue(values[0].AsSpan().SequenceEqual(new byte[] { 1, 2 }));
                AssertTrue(values[1].AsSpan().SequenceEqual(new byte[] { 3, 4 }));
                AssertTrue((await completed.Completion).IsSuccess);
                // Reading a detached result cannot spend a newer activation's byte quota.
                AssertEqual(newer is null ? 0L : 3L, fixture.Session.ReadMetrics().BufferedStreamBytes);
            }
            finally { if (newer is not null) await newer.DisposeAsync(); }
            AssertEqual(0L, fixture.Session.ReadMetrics().BufferedStreamBytes);
            AssertEqual(0, fixture.Session.ReadMetrics().OpenStreams);
        }
    }

    private static async ValueTask SidecarStreamsRejectSequenceCodecAndCreditViolations()
    {
        foreach (string violation in new[] { "sequence", "codec", "credit", "end" })
        {
            await using var fixture = await DataApiSession([StreamApiDeclaration()]);
            await using var stream = (await fixture.Session.OpenStreamByIdAsync(1)).Value;
            var request = await fixture.Peer.ReceiveAsync();
            if (violation == "credit")
            {
                for (ulong sequence = 1; sequence <= 17; sequence++)
                    await DataApiFrame(fixture.Peer, SidecarMessageType.StreamChunk,
                        SidecarStreamMessages.EncodeChunk(sequence, new(SidecarWireCodecs.Bytes, [1])), request.CorrelationId);
            }
            else if (violation == "end")
                await DataApiFrame(fixture.Peer, SidecarMessageType.StreamEnd, SidecarStreamMessages.EncodeEnd(2, true, null), request.CorrelationId);
            else
                await DataApiFrame(fixture.Peer, SidecarMessageType.StreamChunk,
                    SidecarStreamMessages.EncodeChunk(violation == "sequence" ? 2UL : 1UL,
                        violation == "codec" ? new(SidecarWireCodecs.Bool, [1]) : new(SidecarWireCodecs.Bytes, [1])), request.CorrelationId);
            AssertEqual(XsrErrorKind.Unavailable, (await stream.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Error!.Kind);
            await fixture.ReceiveLoop.WaitAsync(TimeSpan.FromSeconds(5));
            AssertEqual(SidecarSessionState.Failed, fixture.Session.State);
            AssertEqual(0L, fixture.Session.ReadMetrics().BufferedStreamBytes); AssertEqual(0, fixture.Session.ReadMetrics().OpenStreams);
        }
    }

    private static async ValueTask SidecarStreamsBoundSessionBytesAndStreamCount()
    {
        await using (var fixture = await DataApiSession([StreamApiDeclaration()]))
        {
            for (int index = 0; index < 16; index++)
            { AssertTrue((await fixture.Session.OpenStreamByIdAsync(1)).IsSuccess); await fixture.Peer.ReceiveAsync(); }
            long sent = fixture.Session.ReadMetrics().SentFrames;
            AssertEqual(XsrErrorKind.Backpressure, (await fixture.Session.OpenStreamByIdAsync(1)).Error!.Kind);
            AssertEqual(sent, fixture.Session.ReadMetrics().SentFrames); AssertEqual(16, fixture.Session.ReadMetrics().OpenStreams);
        }
        await using (var fixture = await DataApiSession([StreamApiDeclaration()]))
        {
            List<(SidecarHostStream Stream, SidecarCorrelationId Correlation)> streams = [];
            for (int index = 0; index < 5; index++)
            {
                var opened = await fixture.Session.OpenStreamByIdAsync(1);
                var request = await fixture.Peer.ReceiveAsync(); streams.Add((opened.Value, request.CorrelationId));
            }
            var payload = new SidecarBinaryValue(SidecarWireCodecs.Bytes, new byte[SidecarWireCodecs.MaximumValueLength]);
            for (int index = 0; index < 4; index++)
                for (ulong sequence = 1; sequence <= 16; sequence++)
                    await DataApiFrame(fixture.Peer, SidecarMessageType.StreamChunk,
                        SidecarStreamMessages.EncodeChunk(sequence, payload), streams[index].Correlation);
            await WaitUntil(() => fixture.Session.ReadMetrics().StreamChunks == 64);
            AssertEqual(64L * SidecarWireCodecs.MaximumValueLength, fixture.Session.ReadMetrics().BufferedStreamBytes);
            await DataApiFrame(fixture.Peer, SidecarMessageType.StreamChunk,
                SidecarStreamMessages.EncodeChunk(1, payload), streams[4].Correlation);
            await fixture.ReceiveLoop.WaitAsync(TimeSpan.FromSeconds(5));
            AssertEqual(SidecarSessionState.Failed, fixture.Session.State);
            AssertEqual(0L, fixture.Session.ReadMetrics().BufferedStreamBytes);
            AssertTrue(streams.All(item => item.Stream.Completion.IsCompleted));
        }
    }

    private static async ValueTask SidecarStreamsCancelTimeoutAndEnforceOneConsumer()
    {
        foreach (string ending in new[] { "cancel", "timeout", "dispose", "deactivate" })
        {
            await using var fixture = await DataApiSession([StreamApiDeclaration()]);
            using var cancelled = new CancellationTokenSource();
            await using var stream = (await fixture.Session.OpenStreamByIdAsync(1,
                timeout: ending == "timeout" ? TimeSpan.FromMilliseconds(100) : null, cancellationToken: cancelled.Token)).Value;
            var request = await fixture.Peer.ReceiveAsync();
            if (ending == "cancel") cancelled.Cancel();
            else if (ending == "dispose") await stream.DisposeAsync();
            else if (ending == "deactivate") await fixture.Session.DeactivateAsync();
            var result = await stream.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            AssertEqual(ending == "timeout" ? XsrErrorKind.TimedOut : ending == "deactivate" ? XsrErrorKind.Unavailable : XsrErrorKind.Cancelled, result.Error!.Kind);
            var frames = new List<SidecarMessageType>();
            while (!frames.Contains(SidecarMessageType.Cancel))
            { var frame = await fixture.Peer.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); frames.Add(frame.MessageType); }
            AssertEqual(0, fixture.Session.ReadMetrics().OpenStreams); AssertEqual(0L, fixture.Session.ReadMetrics().BufferedStreamBytes);
            if (ending == "deactivate")
            {
                if (!frames.Contains(SidecarMessageType.Deactivate)) await fixture.Peer.ReceiveAsync();
                await fixture.Session.ActivateAsync(); await fixture.Peer.ReceiveAsync();
                AssertTrue((await fixture.Session.OpenStreamByIdAsync(1)).IsSuccess); await fixture.Peer.ReceiveAsync();
            }
        }
        await using (var fixture = await DataApiSession([StreamApiDeclaration()]))
        {
            await using var stream = (await fixture.Session.OpenStreamByIdAsync(1)).Value;
            var request = await fixture.Peer.ReceiveAsync();
            await DataApiFrame(fixture.Peer, SidecarMessageType.StreamEnd, SidecarStreamMessages.EncodeEnd(1, true, null), request.CorrelationId);
            await using var first = stream.ReadAllAsync().GetAsyncEnumerator(); AssertFalse(await first.MoveNextAsync());
            await using var second = stream.ReadAllAsync().GetAsyncEnumerator();
            await AssertThrowsAsync<InvalidOperationException>(() => second.MoveNextAsync().AsTask());
        }
    }

    private sealed class StreamOpenFlushBarrier(Stream inner) : Stream
    {
        internal bool Block;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await inner.FlushAsync(cancellationToken);
            if (Block) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
        }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
