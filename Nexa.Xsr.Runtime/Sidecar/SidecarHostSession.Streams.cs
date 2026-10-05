using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;

namespace Nexa.Xsr.Runtime;

public sealed partial class SidecarHostSession
{
    private const int MaximumStreams = 16;
    private const long MaximumBufferedStreamBytes = 4 * 1024 * 1024;
    private readonly Dictionary<Guid, SidecarHostStream> _streams = [];
    private long _bufferedStreamBytes;

    public ValueTask<XsrResult<SidecarHostStream>> OpenStreamAsync(XsrSemanticId stream,
        SidecarBinaryValue? argument = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        OpenStreamCoreAsync(ResolveContract(SidecarRegistrationKind.Stream, stream), argument, timeout, cancellationToken);

    public ValueTask<XsrResult<SidecarHostStream>> OpenStreamByIdAsync(uint contractId,
        SidecarBinaryValue? argument = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        OpenStreamCoreAsync(ResolveContract(SidecarRegistrationKind.Stream, contractId), argument, timeout, cancellationToken);

    private async ValueTask<XsrResult<SidecarHostStream>> OpenStreamCoreAsync(SidecarRegistrationEntry? entry,
        SidecarBinaryValue? argument, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return XsrResult.Failure<SidecarHostStream>(XsrRuntimeErrors.Cancelled());
        lock (_gate)
        {
            if (!Accepting) return XsrResult.Failure<SidecarHostStream>(ExchangeError("xsr.unavailable"));
            if (!NegotiatedFeatures.HasFlag(SidecarFeatures.Streams))
                return XsrResult.Failure<SidecarHostStream>(ExchangeError("xsr.feature_unavailable"));
            if (entry is null) return XsrResult.Failure<SidecarHostStream>(XsrRuntimeErrors.RouteNotFound());
        }
        argument ??= new(0, []);
        if (entry.CodecId != argument.CodecId)
            return XsrResult.Failure<SidecarHostStream>(XsrRuntimeErrors.ContractMismatch());
        TimeSpan bounded = timeout ?? TimeSpan.FromSeconds(30);
        if (bounded <= TimeSpan.Zero) return XsrResult.Failure<SidecarHostStream>(XsrRuntimeErrors.TimedOut());
        if (bounded > TimeSpan.FromMinutes(5)) return XsrResult.Failure<SidecarHostStream>(XsrRuntimeErrors.ContractMismatch());
        var correlation = SidecarCorrelationId.Create();
        var stream = new SidecarHostStream(this, correlation.Value, entry.ResultCodecId);
        CancellationToken activationToken;
        lock (_gate)
        {
            if (!Accepting) return XsrResult.Failure<SidecarHostStream>(ExchangeError("xsr.unavailable"));
            if (_streams.Count >= MaximumStreams)
            { Interlocked.Increment(ref _backpressureCount); return XsrResult.Failure<SidecarHostStream>(XsrRuntimeErrors.Backpressure()); }
            activationToken = _activationEnded.Token;
            _streams.Add(correlation.Value, stream);
        }
        AttachStreamLifetime(stream, bounded, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, activationToken, _sessionEnded.Token, stream.Ended.Token);
        try
        {
            byte[] payload = SidecarStreamMessages.EncodeOpen(entry.ContractId, argument, SidecarStreamMessages.MaximumCredit);
            await SendTrackedAsync(new(SidecarProtocol.Version, SidecarMessageType.StreamOpen, SidecarFrameTraits.None,
                correlation, payload), deadline.Token).ConfigureAwait(false);
            stream.Sent = true;
            // A fast peer may finish an empty stream before the open write's flush resumes.
            lock (_gate) { if (!stream.Retired || stream.RemoteEnded) return XsrResult.Success(stream); }
            await CancelBestEffortAsync(stream.Correlation, "stream ended during opening").ConfigureAwait(false);
            return XsrResult.Failure<SidecarHostStream>((await stream.Completion.ConfigureAwait(false)).Error!);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            string code = error is SidecarTransportBackpressureException ? "xsr.backpressure"
                : cancellationToken.IsCancellationRequested ? "xsr.cancelled" : "xsr.unavailable";
            if (stream.Completion.IsCompleted && !stream.Completion.Result.IsSuccess) code = stream.Completion.Result.Error!.Code.Value;
            await CancelStreamAsync(stream, code, notify: stream.Sent).ConfigureAwait(false);
            if (error is SidecarTransportBackpressureException) Interlocked.Increment(ref _backpressureCount);
            else if (error is not OperationCanceledException && error is not ArgumentException && error is not SidecarProtocolException)
                FailWithMirrorUnavailable("The sidecar stream could not be opened.");
            return XsrResult.Failure<SidecarHostStream>(ExchangeError(code));
        }
    }

    private void AttachStreamLifetime(SidecarHostStream stream, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ITimer timer = _timeProvider.CreateTimer(static state =>
        {
            var item = (SidecarHostStream)state!;
            _ = ObserveBackgroundCleanupAsync(item.Owner.CancelStreamAsync(item, "xsr.timed_out", notify: true, preserveCompleted: true).AsTask());
        }, stream, timeout, Timeout.InfiniteTimeSpan);
        CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(static state =>
        {
            var item = (SidecarHostStream)state!;
            _ = ObserveBackgroundCleanupAsync(item.Owner.CancelStreamAsync(item, "xsr.cancelled", notify: true, preserveCompleted: true).AsTask());
        }, stream);
        bool retired;
        lock (_gate)
        {
            retired = stream.Retired || stream.RemoteEnded;
            if (!retired) { stream.Timer = timer; stream.CallerCancellation = registration; }
        }
        if (retired) { timer.Dispose(); registration.Dispose(); }
    }

    private void AcceptStreamChunk(SidecarFrame frame)
    {
        SidecarHostStream? stream;
        lock (_gate)
        {
            if (!Accepting || !_streams.TryGetValue(frame.CorrelationId.Value, out stream)) return;
        }
        var chunk = SidecarStreamMessages.DecodeChunk(frame.Payload.Span);
        lock (_gate)
        {
            if (!Accepting || stream.Retired) return;
            if (stream.RemoteEnded || chunk.Sequence != stream.NextSequence || chunk.Value.CodecId != stream.CodecId
                || stream.Credit == 0 || chunk.Value.Length > SidecarWireCodecs.MaximumValueLength
                || _bufferedStreamBytes + chunk.Value.Length > MaximumBufferedStreamBytes)
                throw new SidecarProtocolException("Sidecar stream violated its sequence, codec, credit or byte budget.");
            if (!stream.Queue.Writer.TryWrite(chunk.Value))
                throw new SidecarProtocolException("Sidecar stream exceeded its bounded queue.");
            stream.NextSequence++;
            stream.Credit--;
            stream.BufferedBytes += chunk.Value.Length;
            stream.BufferedChunks++;
            _bufferedStreamBytes += chunk.Value.Length;
            Interlocked.Increment(ref _streamChunks);
        }
    }

    private void AcceptStreamEnd(SidecarFrame frame)
    {
        SidecarHostStream? stream;
        lock (_gate)
        {
            if (!Accepting || !_streams.TryGetValue(frame.CorrelationId.Value, out stream)) return;
        }
        var end = SidecarStreamMessages.DecodeEnd(frame.Payload.Span);
        ITimer? timer;
        CancellationTokenRegistration registration;
        lock (_gate)
        {
            if (stream.Retired) return;
            if (stream.RemoteEnded || end.NextSequence != stream.NextSequence)
                throw new SidecarProtocolException("Sidecar stream termination sequence is invalid.");
            stream.RemoteEnded = true;
            timer = stream.Timer; stream.Timer = null;
            registration = stream.CallerCancellation; stream.CallerCancellation = default;
            stream.Queue.Writer.TryComplete();
            stream.CompletionSource.TrySetResult(end.Success ? XsrResult.Success() : XsrResult.Failure(ExchangeError(end.ErrorCode)));
            // Completed but unread chunks remain charged until the one consumer drains or disposes them.
            if (stream.BufferedChunks == 0) { stream.Retired = true; _streams.Remove(stream.Correlation); }
        }
        timer?.Dispose(); registration.Dispose();
    }

    internal async IAsyncEnumerable<SidecarBinaryValue> ReadStreamAsync(SidecarHostStream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref stream.ConsumerStarted, 1) != 0)
            throw new InvalidOperationException("A Sidecar stream permits one asynchronous consumer.");
        try
        {
            while (await stream.Queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                SidecarBinaryValue? value;
                bool credit;
                lock (_gate)
                {
                    if (!stream.Queue.Reader.TryRead(out value)) continue;
                    stream.BufferedBytes -= value.Length;
                    stream.BufferedChunks--;
                    if (!stream.Detached) _bufferedStreamBytes -= value.Length;
                    credit = !stream.Retired && !stream.RemoteEnded && Accepting;
                    if (credit) stream.Credit++;
                    if (stream.RemoteEnded && stream.BufferedChunks == 0)
                    { stream.Retired = true; _streams.Remove(stream.Correlation); }
                }
                if (credit)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stream.Ended.Token, _sessionEnded.Token);
                    deadline.CancelAfter(TimeSpan.FromMilliseconds(100));
                    try
                    {
                        await SendTrackedAsync(new(SidecarProtocol.Version, SidecarMessageType.StreamCredit, SidecarFrameTraits.None,
                        new(stream.Correlation), SidecarStreamMessages.EncodeCredit(1)), deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stream.Ended.IsCancellationRequested || _sessionEnded.IsCancellationRequested) { }
                    catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
                    { FailWithMirrorUnavailable("The sidecar stream credit could not be delivered."); }
                }
                yield return value;
            }
        }
        finally
        {
            await CancelStreamAsync(stream, "xsr.cancelled", notify: true).ConfigureAwait(false);
        }
    }

    internal async ValueTask CancelStreamAsync(SidecarHostStream stream, string code, bool notify, bool preserveCompleted = false)
    {
        ITimer? timer;
        CancellationTokenRegistration registration;
        bool send;
        lock (_gate)
        {
            if (stream.Retired) return;
            if (preserveCompleted && stream.RemoteEnded)
            {
                _streams.Remove(stream.Correlation);
                if (!stream.Detached) _bufferedStreamBytes -= stream.BufferedBytes;
                stream.Detached = true;
                return;
            }
            stream.Retired = true;
            _streams.Remove(stream.Correlation);
            if (!stream.Detached) _bufferedStreamBytes -= stream.BufferedBytes;
            stream.BufferedBytes = 0;
            stream.BufferedChunks = 0;
            while (stream.Queue.Reader.TryRead(out _)) { }
            stream.Queue.Writer.TryComplete();
            stream.CompletionSource.TrySetResult(XsrResult.Failure(ExchangeError(code)));
            timer = stream.Timer; stream.Timer = null;
            registration = stream.CallerCancellation; stream.CallerCancellation = default;
            send = notify && stream.Sent && !_stopped && !stream.RemoteEnded;
            if (code == "xsr.cancelled") Interlocked.Increment(ref _cancelledCount);
            else if (code == "xsr.timed_out") Interlocked.Increment(ref _timedOutCount);
        }
        stream.Ended.Cancel();
        timer?.Dispose();
        // Non-blocking unregister avoids waiting for this cancellation callback itself.
        registration.Unregister();
        if (send) await CancelBestEffortAsync(stream.Correlation, "host stream ended").ConfigureAwait(false);
    }

    private void EndStreams(string code)
    {
        SidecarHostStream[] streams;
        lock (_gate)
        {
            streams = _streams.Values.Where(stream => !stream.RemoteEnded).ToArray();
            foreach (var stream in _streams.Values.Where(stream => stream.RemoteEnded).ToArray())
            {
                // The remote result is final. Its already admitted immutable chunks now belong
                // to the returned handle and must remain readable after host/session retirement.
                _streams.Remove(stream.Correlation);
                if (!stream.Detached) _bufferedStreamBytes -= stream.BufferedBytes;
                stream.Detached = true;
            }
        }
        foreach (var stream in streams)
            _ = ObserveBackgroundCleanupAsync(CancelStreamAsync(stream, code, notify: !Volatile.Read(ref _stopped), preserveCompleted: true).AsTask());
    }

    private static async Task ObserveBackgroundCleanupAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { /* Stream ownership and completion are retired before external cleanup runs. */ }
    }
}

/// <summary>One owned stream. ReadAllAsync has one consumer; Completion reports its stable final outcome.</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "This is a credit-controlled Sidecar message stream with one asynchronous consumer, not a System.IO byte stream.")]
public sealed class SidecarHostStream : IAsyncDisposable
{
    internal SidecarHostStream(SidecarHostSession owner, Guid correlation, uint codecId)
    { Owner = owner; Correlation = correlation; CodecId = codecId; }
    internal SidecarHostSession Owner { get; }
    internal Guid Correlation { get; }
    public uint CodecId { get; }
    internal Channel<SidecarBinaryValue> Queue { get; } = Channel.CreateBounded<SidecarBinaryValue>(new BoundedChannelOptions(16)
    { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    internal TaskCompletionSource<XsrResult> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationTokenSource Ended { get; } = new();
    internal CancellationTokenRegistration CallerCancellation;
    internal ITimer? Timer;
    internal uint Credit = SidecarStreamMessages.MaximumCredit;
    internal ulong NextSequence = 1;
    internal long BufferedBytes;
    internal int BufferedChunks;
    internal bool Retired, RemoteEnded, Detached;
    internal volatile bool Sent;
    internal int ConsumerStarted;
    public Task<XsrResult> Completion => CompletionSource.Task;
    public IAsyncEnumerable<SidecarBinaryValue> ReadAllAsync(CancellationToken cancellationToken = default) =>
        Owner.ReadStreamAsync(this, cancellationToken);
    public ValueTask DisposeAsync() => Owner.CancelStreamAsync(this, "xsr.cancelled", notify: true);
}
