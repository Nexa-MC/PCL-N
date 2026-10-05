using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;

namespace Nexa.Xsr.Runtime;

/// <summary>Bounded diagnostics contain counters and host timestamps, never arguments or payloads.</summary>
public sealed record SidecarSessionMetrics(SidecarSessionState State, int PendingExchanges, int PendingHealthChecks,
    int OpenStreams, long BufferedStreamBytes, long SentFrames, long ReceivedFrames, long SentPayloadBytes,
    long ReceivedPayloadBytes, long ExchangesStarted, long ExchangesCompleted, long Cancelled, long TimedOut,
    long Backpressure, long LateResults, long StateDeltas, long EventsDelivered, long StreamChunks,
    long Crashes, DateTimeOffset? LastActivityUtc, TimeSpan? LastHealthRoundTrip, string? LastRemoteError)
{
    public DateTimeOffset? LastHealthUtc { get; init; }
    public bool? LastHealthSucceeded { get; init; }
}

public sealed partial class SidecarHostSession
{
    private readonly Dictionary<Guid, PendingHealth> _healthPending = [];
    private long _healthNonce;
    private long _sentFrames, _receivedFrames, _sentPayloadBytes, _receivedPayloadBytes;
    private long _exchangesStarted, _exchangesCompleted, _cancelledCount, _timedOutCount, _backpressureCount;
    private long _lateResults, _stateDeltas, _eventsDelivered, _streamChunks, _crashes;
    private DateTimeOffset? _lastActivity;
    private TimeSpan? _lastHealthRoundTrip;
    private DateTimeOffset? _lastHealthUtc;
    private bool? _lastHealthSucceeded;
    private string? _lastRemoteError;

    public SidecarSessionMetrics ReadMetrics()
    {
        lock (_gate) return new(_state, _pending.Count, _healthPending.Count, _streams.Count, _bufferedStreamBytes,
            Interlocked.Read(ref _sentFrames), Interlocked.Read(ref _receivedFrames), Interlocked.Read(ref _sentPayloadBytes),
            Interlocked.Read(ref _receivedPayloadBytes), Interlocked.Read(ref _exchangesStarted), Interlocked.Read(ref _exchangesCompleted),
            Interlocked.Read(ref _cancelledCount), Interlocked.Read(ref _timedOutCount), Interlocked.Read(ref _backpressureCount),
            Interlocked.Read(ref _lateResults), Interlocked.Read(ref _stateDeltas), Interlocked.Read(ref _eventsDelivered),
            Interlocked.Read(ref _streamChunks), Interlocked.Read(ref _crashes), _lastActivity, _lastHealthRoundTrip, _lastRemoteError)
        { LastHealthUtc = _lastHealthUtc, LastHealthSucceeded = _lastHealthSucceeded };
    }

    private async ValueTask SendTrackedAsync(SidecarFrame frame, CancellationToken cancellationToken = default)
    {
        await _connection.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _sentFrames);
        Interlocked.Add(ref _sentPayloadBytes, frame.Payload.Length);
        lock (_gate) _lastActivity = _timeProvider.GetUtcNow();
    }

    private void RecordReceivedFrame(SidecarFrame frame)
    {
        Interlocked.Increment(ref _receivedFrames);
        Interlocked.Add(ref _receivedPayloadBytes, frame.Payload.Length);
        lock (_gate) _lastActivity = _timeProvider.GetUtcNow();
    }

    /// <summary>Checks negotiated transport liveness without running a business command.</summary>
    public async ValueTask<XsrResult<TimeSpan>> PingAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return XsrResult.Failure<TimeSpan>(XsrRuntimeErrors.Cancelled());
        TimeSpan bounded = timeout ?? TimeSpan.FromSeconds(5);
        if (bounded <= TimeSpan.Zero) return XsrResult.Failure<TimeSpan>(XsrRuntimeErrors.TimedOut());
        if (bounded > TimeSpan.FromMinutes(5)) return XsrResult.Failure<TimeSpan>(XsrRuntimeErrors.ContractMismatch());
        using var timedOut = new CancellationTokenSource(bounded, _timeProvider);
        var correlation = SidecarCorrelationId.Create();
        var health = new PendingHealth(unchecked((ulong)Interlocked.Increment(ref _healthNonce)), _timeProvider.GetTimestamp());
        CancellationToken activationToken;
        lock (_gate)
        {
            if (!Accepting) return XsrResult.Failure<TimeSpan>(ExchangeError("xsr.unavailable"));
            if (!NegotiatedFeatures.HasFlag(SidecarFeatures.Health)) return XsrResult.Failure<TimeSpan>(ExchangeError("xsr.feature_unavailable"));
            if (_healthPending.Count >= 32 || _pending.Count + _healthPending.Count >= _maxPending)
            { Interlocked.Increment(ref _backpressureCount); return XsrResult.Failure<TimeSpan>(XsrRuntimeErrors.Backpressure()); }
            activationToken = _activationEnded.Token;
            _healthPending.Add(correlation.Value, health);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timedOut.Token, activationToken, _sessionEnded.Token);
        try
        {
            await SendTrackedAsync(new(SidecarProtocol.Version, SidecarMessageType.HealthPing, SidecarFrameTraits.None,
                correlation, SidecarControlMessages.EncodeHealth(health.Nonce)), deadline.Token).ConfigureAwait(false);
            return await health.Completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            RecordHealthOutcome(false);
            if (error is SidecarTransportBackpressureException)
            { Interlocked.Increment(ref _backpressureCount); return XsrResult.Failure<TimeSpan>(XsrRuntimeErrors.Backpressure()); }
            if (cancellationToken.IsCancellationRequested)
            { Interlocked.Increment(ref _cancelledCount); return XsrResult.Failure<TimeSpan>(XsrRuntimeErrors.Cancelled()); }
            if (timedOut.IsCancellationRequested)
            { Interlocked.Increment(ref _timedOutCount); return XsrResult.Failure<TimeSpan>(XsrRuntimeErrors.TimedOut()); }
            if (!activationToken.IsCancellationRequested && !_sessionEnded.IsCancellationRequested)
                FailWithMirrorUnavailable("The sidecar health exchange failed.");
            return XsrResult.Failure<TimeSpan>(ExchangeError("xsr.unavailable"));
        }
        finally { lock (_gate) _healthPending.Remove(correlation.Value); }
    }

    private async ValueTask RespondToHealthPingAsync(SidecarFrame frame, CancellationToken cancellationToken)
    {
        if (!NegotiatedFeatures.HasFlag(SidecarFeatures.Health))
            throw new SidecarProtocolException("Sidecar health was not negotiated.");
        ulong nonce = SidecarControlMessages.DecodeHealth(frame.Payload.Span);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(100));
        await SendTrackedAsync(new(SidecarProtocol.Version, SidecarMessageType.HealthPong, SidecarFrameTraits.None,
            frame.CorrelationId, SidecarControlMessages.EncodeHealth(nonce)), deadline.Token).ConfigureAwait(false);
    }

    private void CompleteHealthPing(SidecarFrame frame)
    {
        PendingHealth? health;
        lock (_gate)
        {
            if (!_healthPending.TryGetValue(frame.CorrelationId.Value, out health))
            { Interlocked.Increment(ref _lateResults); return; }
        }
        ulong nonce = SidecarControlMessages.DecodeHealth(frame.Payload.Span);
        if (nonce != health.Nonce) throw new SidecarProtocolException("Sidecar health nonce does not match its request.");
        var elapsed = _timeProvider.GetElapsedTime(health.Started);
        lock (_gate)
        {
            if (!_healthPending.Remove(frame.CorrelationId.Value)) return;
            _lastHealthRoundTrip = elapsed;
            _lastHealthUtc = _timeProvider.GetUtcNow();
            _lastHealthSucceeded = true;
        }
        health.Completion.TrySetResult(XsrResult.Success(elapsed));
    }

    private void EndHealthPings()
    {
        PendingHealth[] health;
        lock (_gate) { health = _healthPending.Values.ToArray(); _healthPending.Clear(); }
        if (health.Length != 0) RecordHealthOutcome(false);
        foreach (var item in health) item.Completion.TrySetResult(XsrResult.Failure<TimeSpan>(ExchangeError("xsr.unavailable")));
    }

    private void RecordHealthOutcome(bool succeeded)
    { lock (_gate) { _lastHealthUtc = _timeProvider.GetUtcNow(); _lastHealthSucceeded = succeeded; } }

    private sealed class PendingHealth(ulong nonce, long started)
    {
        internal ulong Nonce { get; } = nonce;
        internal long Started { get; } = started;
        internal TaskCompletionSource<XsrResult<TimeSpan>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
