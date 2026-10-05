using System.Buffers.Binary;
using System.Net.Sockets;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;

namespace Nexa.Sidecar.Tests;

internal static partial class Program
{
    private static async ValueTask InvalidFrameHeaderFailsBeforeBodyRead()
    {
        foreach (int offset in new[] { 4, 6, 8, 10, 12 })
        {
            byte[] header = EncodeFrame(BuildFrame())[..SidecarProtocol.HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), SidecarProtocol.MaxPayloadLength);
            if (offset == 12) header.AsSpan(12, 16).Clear();
            else { header[offset] = 255; header[offset + 1] = 255; }
            using var stream = new PrefixBlockingStream(header);
            using var connection = new SidecarConnection(stream);
            await AssertThrowsAsync<SidecarProtocolException>(() => connection.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            AssertEqual(1, stream.ReadCalls);
            AssertEqual(SidecarConnectionState.Failed, connection.State);
            AssertTrue(stream.Closed);
        }
    }

    private static async ValueTask PartialFrameReadCancellationPoisonsConnection()
    {
        byte[] wire = EncodeFrame(BuildFrame());
        foreach (int prefixLength in new[] { 8, SidecarProtocol.HeaderSize + 2 })
        {
            using var stream = new PrefixBlockingStream(wire[..prefixLength]);
            using var connection = new SidecarConnection(stream);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task<SidecarFrame> reading = connection.ReceiveAsync(deadline.Token).AsTask();
            await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            deadline.Cancel();
            await AssertThrowsAsync<IOException>(() => reading);
            AssertEqual(SidecarConnectionState.Failed, connection.State);
            AssertTrue(stream.Closed);
            await AssertThrowsAsync<InvalidOperationException>(() => connection.ReceiveAsync().AsTask());
        }
    }

    private static async ValueTask PartialFrameWriteCancellationPoisonsConnection()
    {
        using var stream = new BlockingWriteStream(8);
        using var connection = new SidecarConnection(stream);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task writing = connection.SendAsync(BuildFrame(), cancellation.Token).AsTask();
        await stream.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AssertEqual(8, stream.BytesWritten);
        cancellation.Cancel();
        await AssertThrowsAsync<IOException>(() => writing);
        AssertEqual(SidecarConnectionState.Failed, connection.State);
        AssertTrue(stream.Closed);
    }

    private static async ValueTask PreReadCancellationPreservesConnection()
    {
        var (first, second) = SidecarLoopbackStream.CreatePair();
        using var connection = new SidecarConnection(first);
        using var peer = new SidecarConnection(second);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => connection.ReceiveAsync(cancellation.Token).AsTask());
        AssertEqual(SidecarConnectionState.Connected, connection.State);
        await peer.SendAsync(BuildFrame());
        AssertEqual(SidecarMessageType.CommandRequest, (await ReceiveAsync(connection)).MessageType);
        AssertEqual(0, await first.ReadAsync(Memory<byte>.Empty));
        await AssertThrowsAsync<OperationCanceledException>(() => second.WriteAsync(new byte[] { 1 }, cancellation.Token).AsTask());
    }

    private static async ValueTask WriteAdmissionAndWaitCancellationPreserveConnection()
    {
        using var stream = new BlockingWriteStream(0);
        using var connection = new SidecarConnection(stream);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task first = connection.SendAsync(BuildFrame()).AsTask();
        await stream.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task[] queued = Enumerable.Range(1, SidecarFrameTransport.MaximumPendingWrites - 1)
            .Select(_ => connection.SendAsync(BuildFrame(), cancellation.Token).AsTask()).ToArray();
        await AssertThrowsAsync<SidecarTransportBackpressureException>(() => connection.SendAsync(BuildFrame()).AsTask());
        AssertEqual(SidecarConnectionState.Connected, connection.State);
        AssertEqual(1, stream.WriteCalls);
        cancellation.Cancel();
        foreach (Task waiting in queued) await AssertThrowsAsync<OperationCanceledException>(() => waiting);
        AssertEqual(SidecarConnectionState.Connected, connection.State);
        stream.Release.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await connection.SendAsync(BuildFrame());
        AssertEqual(2, stream.WriteCalls);
        AssertEqual(SidecarConnectionState.Connected, connection.State);
        // Local frame validation is also before IO and cannot retire this live connection.
        await AssertThrowsAsync<SidecarProtocolException>(() => connection.SendAsync(BuildFrame() with { CorrelationId = default }).AsTask());
        AssertEqual(2, stream.WriteCalls);
        AssertEqual(SidecarConnectionState.Connected, connection.State);
    }

    private static async ValueTask IpcAcceptCancellationAndDisposalOwnResources()
    {
        using var listener = SidecarIpcListener.Bind("completion-" + Guid.NewGuid().ToString("N"));
        using var firstCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<Stream> firstAccept = listener.AcceptAsync(firstCancellation.Token).AsTask();
        await AssertThrowsAsync<InvalidOperationException>(() => listener.AcceptAsync().AsTask());
        firstCancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => firstAccept);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<Stream> accepted = listener.AcceptAsync(deadline.Token).AsTask();
        using Stream client = await SidecarIpcConnector.ConnectAsync(listener.Endpoint, deadline.Token);
        using Stream server = await accepted;
        string endpoint = listener.Endpoint;
        listener.Dispose();
        await AssertThrowsAsync<ObjectDisposedException>(() => listener.AcceptAsync().AsTask());
        if (!OperatingSystem.IsWindows()) AssertFalse(File.Exists(endpoint));
        byte[] received = new byte[1];
        Task reading = server.ReadExactlyAsync(received, deadline.Token).AsTask();
        await client.WriteAsync(new byte[] { 123 }, deadline.Token);
        await reading;
        AssertEqual((byte)123, received[0]);
    }

    private static async ValueTask FailedUnixConnectDoesNotLeakSockets()
    {
        if (!OperatingSystem.IsLinux()) return;
        string missing = Path.Combine(Path.GetTempPath(), "missing-nexa-" + Guid.NewGuid().ToString("N"));
        // Warm the socket/connect and cancellation infrastructure before measuring physical handles.
        await AssertThrowsAsync<SocketException>(() => SidecarIpcConnector.ConnectAsync(missing).AsTask());
        int before = Directory.EnumerateFiles("/proc/self/fd").Count();
        for (int index = 0; index < 128; index++)
            await AssertThrowsAsync<SocketException>(() => SidecarIpcConnector.ConnectAsync(missing).AsTask());
        int after = Directory.EnumerateFiles("/proc/self/fd").Count();
        AssertTrue(after <= before + 2);
        AssertFalse(File.Exists(missing));
    }

    private abstract class CompletionStream : Stream
    {
        public bool Closed { get; protected set; }
        public override bool CanRead => !Closed;
        public override bool CanWrite => !Closed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }

    private sealed class PrefixBlockingStream(byte[] prefix) : CompletionStream
    {
        private int _offset;
        public int ReadCalls { get; private set; }
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            if (_offset < prefix.Length)
            {
                int length = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, length).CopyTo(buffer);
                _offset += length;
                return length;
            }
            Blocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The controlled read cannot complete without cancellation.");
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class BlockingWriteStream(int prefixBytes) : CompletionStream
    {
        public TaskCompletionSource FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCalls { get; private set; }
        public int BytesWritten { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            BytesWritten += Math.Min(prefixBytes, buffer.Length);
            FirstWrite.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (Closed) throw new IOException("The controlled writer is closed.");
            BytesWritten += buffer.Length - Math.Min(prefixBytes, buffer.Length);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Release.TrySetResult();
        }
    }
}
