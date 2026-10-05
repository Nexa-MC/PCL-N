using Nexa.Sidecar.Protocol;

namespace Nexa.Sidecar.Transport;

/// <summary>
/// The connection lifecycle. Every transition is explicit; a protocol failure moves the
/// connection to <see cref="Failed"/> and it never reconnects itself.
/// </summary>
public enum SidecarConnectionState
{
    Connected = 1,
    Closed = 2,
    Failed = 3,
}

/// <summary>
/// Reads and writes Sidecar frames over one duplex stream. Writes are serialized internally so
/// concurrent senders cannot interleave frame bytes; reads are single-caller. Protocol errors
/// surface as <see cref="SidecarProtocolException"/> and poison the stream.
/// </summary>
public sealed class SidecarFrameTransport : IDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly object _lifetimeGate = new();
    private int _operations;
    private int _pendingWrites;
    private bool _disposed;
    public const int MaximumPendingWrites = 64;

    public SidecarFrameTransport(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead || !stream.CanWrite)
        {
            throw new ArgumentException("The sidecar stream must be duplex.", nameof(stream));
        }
    }

    /// <summary>
    /// Writes one frame atomically.
    /// </summary>
    public async ValueTask SendAsync(
        SidecarFrame frame,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeginOperation();
        bool admitted = false;
        bool ownsWriteGate = false;
        try
        {
            if (Interlocked.Increment(ref _pendingWrites) > MaximumPendingWrites)
            {
                Interlocked.Decrement(ref _pendingWrites);
                throw new SidecarTransportBackpressureException();
            }
            admitted = true;
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            ownsWriteGate = true;
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            byte[] wire = new byte[SidecarFrameCodec.GetFrameSize(frame.Payload.Length)];
            SidecarFrameCodec.Encode(frame, wire);
            try
            {
                await _stream.WriteAsync(wire, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException error)
            {
                // A partial frame cannot be resumed or followed by another frame safely.
                _stream.Close();
                throw new IOException("The sidecar frame write was interrupted.", error);
            }
            catch
            {
                _stream.Close();
                throw;
            }
        }
        finally
        {
            if (ownsWriteGate) _writeGate.Release();
            if (admitted) Interlocked.Decrement(ref _pendingWrites);
            EndOperation();
        }
    }

    /// <summary>
    /// Reads exactly one frame, blocking until the header and payload have arrived.
    /// </summary>
    public async ValueTask<SidecarFrame> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeginOperation();
        bool ownsReadGate = false;
        try
        {
            await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            ownsReadGate = true;
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            try
            {
                byte[] header = new byte[SidecarProtocol.HeaderSize];
                await ReadExactAsync(header, cancellationToken).ConfigureAwait(false);
                int payloadLength = SidecarFrameCodec.ValidateHeader(header);
                byte[] wire = new byte[SidecarProtocol.HeaderSize + payloadLength];
                header.CopyTo(wire, 0);
                await ReadExactAsync(wire.AsMemory(SidecarProtocol.HeaderSize), cancellationToken).ConfigureAwait(false);
                return SidecarFrameCodec.Decode(wire);
            }
            catch (OperationCanceledException error)
            {
                _stream.Close();
                throw new IOException("The sidecar frame read was interrupted.", error);
            }
            catch
            {
                _stream.Close();
                throw;
            }
        }
        finally
        {
            if (ownsReadGate) _readGate.Release();
            EndOperation();
        }
    }

    private async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = await _stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (chunk == 0)
            {
                throw new EndOfStreamException(
                    $"The sidecar stream ended after {read} of {buffer.Length} expected bytes.");
            }

            read += chunk;
        }
    }

    /// <summary>
    /// Rejects new operations and retires both gates when admitted operations have finished.
    /// </summary>
    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_operations == 0) DisposeGates();
        }
    }

    private void BeginOperation()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _operations++;
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_lifetimeGate) ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void EndOperation()
    {
        lock (_lifetimeGate)
        {
            _operations--;
            if (_disposed && _operations == 0) DisposeGates();
        }
    }

    private void DisposeGates()
    {
        _writeGate.Dispose();
        _readGate.Dispose();
    }
}

/// <summary>Bounded local write admission rejected the frame before any bytes were sent.</summary>
public sealed class SidecarTransportBackpressureException : InvalidOperationException
{
    public SidecarTransportBackpressureException() : base("The sidecar transport write admission limit was reached.") { }
}
