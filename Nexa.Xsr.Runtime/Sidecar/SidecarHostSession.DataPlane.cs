using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr.State;

namespace Nexa.Xsr.Runtime;

/// <summary>One numeric, bounded data plane. The receive loop is the connection's only runtime reader.</summary>
public sealed partial class SidecarHostSession
{
    private readonly Dictionary<Guid, PendingExchange> _pending = [];
    private readonly CancellationTokenSource _sessionEnded = new();
    private CancellationTokenSource _activationEnded = new();
    private bool _stopped, _activationAccepting, _admissionClosed;
    private readonly object _mirrorGate = new();
    private readonly int _maxPending;
    private int _receiveLoopStarted;
    private ISidecarSessionEventObserver? _eventObserver;
    private ISidecarSessionBinaryEventObserver? _binaryEventObserver;

    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>Installs a host observer. Callback failures do not change ordered delivery.</summary>
    public void AttachEventObserver(ISidecarSessionEventObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        Volatile.Write(ref _eventObserver, observer);
    }

    public void AttachBinaryEventObserver(ISidecarSessionBinaryEventObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        Volatile.Write(ref _binaryEventObserver, observer);
    }

    public ValueTask<XsrResult> SendCommandAsync(XsrSemanticId command, string? argument = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        SendStringCommandAsync(ResolveContract(SidecarRegistrationKind.Command, command), argument, timeout, cancellationToken);

    public ValueTask<XsrResult> SendCommandByIdAsync(uint contractId, string? argument = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        SendStringCommandAsync(ResolveContract(SidecarRegistrationKind.Command, contractId), argument, timeout, cancellationToken);

    private async ValueTask<XsrResult> SendStringCommandAsync(SidecarRegistrationEntry? entry,
        string? argument, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var result = await SendStringAsync(SidecarMessageType.CommandRequest, entry, argument, timeout, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? XsrResult.Success() : XsrResult.Failure(result.Error!);
    }

    public ValueTask<XsrResult<string>> SendQueryAsync(XsrSemanticId query, string? argument = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        SendStringAsync(SidecarMessageType.QueryRequest, ResolveContract(SidecarRegistrationKind.Query, query), argument, timeout, cancellationToken);

    public ValueTask<XsrResult<string>> SendQueryByIdAsync(uint contractId, string? argument = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        SendStringAsync(SidecarMessageType.QueryRequest, ResolveContract(SidecarRegistrationKind.Query, contractId), argument, timeout, cancellationToken);

    private async ValueTask<XsrResult<string>> SendStringAsync(SidecarMessageType requestType,
        SidecarRegistrationEntry? entry, string? argument, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        XsrError? rejected = ValidateRequest(entry, binary: false, cancellationToken);
        if (rejected is not null) return XsrResult.Failure<string>(rejected);
        byte[] payload;
        try { payload = SidecarDataMessages.EncodeRequest(entry!.ContractId, argument); }
        catch (Exception error) when (error is ArgumentException or SidecarProtocolException)
        { return XsrResult.Failure<string>(XsrRuntimeErrors.ContractMismatch()); }
        ExchangeOutcome outcome = await RunExchangeAsync(requestType, entry!, payload, timeout, cancellationToken).ConfigureAwait(false);
        return outcome.Success ? XsrResult.Success(outcome.Value!.CodecId == 0
            ? System.Text.Encoding.UTF8.GetString(outcome.Value.Span) : string.Empty)
            : XsrResult.Failure<string>(ExchangeError(outcome.ErrorCode));
    }

    public ValueTask<XsrResult<SidecarBinaryValue>> SendBinaryCommandAsync(XsrSemanticId command,
        SidecarBinaryValue? argument = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        SendBinaryAsync(SidecarMessageType.CommandRequest, ResolveContract(SidecarRegistrationKind.Command, command), argument, timeout, cancellationToken);

    public ValueTask<XsrResult<SidecarBinaryValue>> SendBinaryCommandByIdAsync(uint contractId,
        SidecarBinaryValue? argument = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        SendBinaryAsync(SidecarMessageType.CommandRequest, ResolveContract(SidecarRegistrationKind.Command, contractId), argument, timeout, cancellationToken);

    public ValueTask<XsrResult<SidecarBinaryValue>> SendBinaryQueryAsync(XsrSemanticId query,
        SidecarBinaryValue? argument = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        SendBinaryAsync(SidecarMessageType.QueryRequest, ResolveContract(SidecarRegistrationKind.Query, query), argument, timeout, cancellationToken);

    public ValueTask<XsrResult<SidecarBinaryValue>> SendBinaryQueryByIdAsync(uint contractId,
        SidecarBinaryValue? argument = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        SendBinaryAsync(SidecarMessageType.QueryRequest, ResolveContract(SidecarRegistrationKind.Query, contractId), argument, timeout, cancellationToken);

    private async ValueTask<XsrResult<SidecarBinaryValue>> SendBinaryAsync(SidecarMessageType requestType,
        SidecarRegistrationEntry? entry, SidecarBinaryValue? argument, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        XsrError? rejected = ValidateRequest(entry, binary: true, cancellationToken);
        if (rejected is not null) return XsrResult.Failure<SidecarBinaryValue>(rejected);
        argument ??= new(0, []);
        if (argument.CodecId != entry!.CodecId) return XsrResult.Failure<SidecarBinaryValue>(XsrRuntimeErrors.ContractMismatch());
        byte[] payload;
        try { payload = SidecarDataMessages.EncodeBinaryRequest(entry.ContractId, argument); }
        catch (Exception error) when (error is ArgumentException or SidecarProtocolException)
        { return XsrResult.Failure<SidecarBinaryValue>(XsrRuntimeErrors.ContractMismatch()); }
        ExchangeOutcome outcome = await RunExchangeAsync(requestType, entry, payload, timeout, cancellationToken).ConfigureAwait(false);
        return outcome.Success ? XsrResult.Success(outcome.Value!) : XsrResult.Failure<SidecarBinaryValue>(ExchangeError(outcome.ErrorCode));
    }

    private SidecarRegistrationEntry? ResolveContract(SidecarRegistrationKind kind, XsrSemanticId semantic)
    { lock (_gate) return _registration?.TryResolve(kind, semantic); }

    private SidecarRegistrationEntry? ResolveContract(SidecarRegistrationKind kind, uint contractId)
    { lock (_gate) return _registration?.TryResolveId(kind, contractId); }

    private bool Accepting => !_stopped && !_admissionClosed && _activationAccepting && _state == SidecarSessionState.Active;

    private XsrError? ValidateRequest(SidecarRegistrationEntry? entry, bool binary, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return XsrRuntimeErrors.Cancelled();
        lock (_gate)
        {
            if (!Accepting) return ExchangeError("xsr.unavailable");
            if (entry is null) return XsrRuntimeErrors.RouteNotFound();
            if (binary && !NegotiatedFeatures.HasFlag(SidecarFeatures.BinaryPayloads)) return ExchangeError("xsr.feature_unavailable");
            if (!binary && (entry.CodecId != 0 || entry.ResultCodecId != 0)) return XsrRuntimeErrors.ContractMismatch();
            return null;
        }
    }

    public async ValueTask RunReceiveLoopAsync(CancellationToken cancellationToken = default)
    {
        if (State is not (SidecarSessionState.Ready or SidecarSessionState.Active))
            throw new InvalidOperationException("The receive loop requires a ready or active session.");
        if (Interlocked.CompareExchange(ref _receiveLoopStarted, 1, 0) != 0)
            throw new InvalidOperationException("The session already owns a receive loop.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionEnded.Token);
        try { await RunReceiveLoopCoreAsync(linked.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { FailWithMirrorUnavailable("The sidecar receive loop terminated."); }
    }

    private async ValueTask RunReceiveLoopCoreAsync(CancellationToken cancellationToken)
    {
        while (!Volatile.Read(ref _stopped))
        {
            SidecarFrame frame = await _connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            RecordReceivedFrame(frame);
            switch (frame.MessageType)
            {
                case SidecarMessageType.CommandResult or SidecarMessageType.QueryResult: CompleteExchange(frame); break;
                case SidecarMessageType.StateDelta: ApplyStateDelta(frame.Payload.Span); break;
                case SidecarMessageType.Event: DeliverEvent(frame.Payload.Span); break;
                case SidecarMessageType.HealthPing: await RespondToHealthPingAsync(frame, cancellationToken).ConfigureAwait(false); break;
                case SidecarMessageType.HealthPong: CompleteHealthPing(frame); break;
                case SidecarMessageType.StreamChunk: AcceptStreamChunk(frame); break;
                case SidecarMessageType.StreamEnd: AcceptStreamEnd(frame); break;
                case SidecarMessageType.Unregister or SidecarMessageType.Unregistered:
                    await HandleLifecycleControlAsync(frame, cancellationToken).ConfigureAwait(false); break;
                case SidecarMessageType.Error:
                    HandleRemoteFailure(frame, crashed: false); break;
                case SidecarMessageType.Crash:
                    HandleRemoteFailure(frame, crashed: true); return;
                case SidecarMessageType.Shutdown:
                    Transition(SidecarSessionState.Closed); EndPending(); _connection.Close(); return;
                default: throw new SidecarProtocolException("Unexpected sidecar runtime message.");
            }
        }
    }

    private async ValueTask<ExchangeOutcome> RunExchangeAsync(SidecarMessageType requestType,
        SidecarRegistrationEntry entry, byte[] payload, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return ExchangeOutcome.Failed("xsr.cancelled");
        TimeSpan bounded = timeout ?? TimeSpan.FromSeconds(30);
        if (bounded <= TimeSpan.Zero) return ExchangeOutcome.Failed("xsr.timed_out");
        // Preserve the original positive timeout range accepted by .NET timers.
        if (bounded.TotalMilliseconds > uint.MaxValue - 1d) return ExchangeOutcome.Failed("xsr.contract_mismatch");
        using var timeoutSource = new CancellationTokenSource(bounded, _timeProvider);
        SidecarCorrelationId correlation = SidecarCorrelationId.Create();
        PendingExchange exchange = new(requestType == SidecarMessageType.CommandRequest
            ? SidecarMessageType.CommandResult : SidecarMessageType.QueryResult, entry.ResultCodecId);
        CancellationToken activationToken;
        lock (_gate)
        {
            if (!Accepting) return ExchangeOutcome.Failed("xsr.unavailable");
            if (_pending.Count + _healthPending.Count >= _maxPending)
            { Interlocked.Increment(ref _backpressureCount); return ExchangeOutcome.Failed("xsr.backpressure"); }
            activationToken = _activationEnded.Token;
            _pending.Add(correlation.Value, exchange);
            Interlocked.Increment(ref _exchangesStarted);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionEnded.Token, activationToken, timeoutSource.Token);
        try
        {
            await SendTrackedAsync(new(SidecarProtocol.Version, requestType, SidecarFrameTraits.None, correlation, payload), deadline.Token).ConfigureAwait(false);
            exchange.Sent = true;
            return await exchange.Completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            if (error is SidecarTransportBackpressureException)
            { Interlocked.Increment(ref _backpressureCount); return ExchangeOutcome.Failed("xsr.backpressure"); }
            bool expired = timeoutSource.IsCancellationRequested || cancellationToken.IsCancellationRequested;
            bool stopped = _sessionEnded.IsCancellationRequested || activationToken.IsCancellationRequested;
            if ((!exchange.Sent && error is not OperationCanceledException) || (exchange.Sent && !expired && !stopped))
                FailWithMirrorUnavailable("The sidecar exchange could not be transmitted or completed.");
            else if (exchange.Sent && !stopped)
            {
                using var cancelDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await SendCancelAsync(correlation, "host exchange ended", cancelDeadline.Token).ConfigureAwait(false);
            }
            if (cancellationToken.IsCancellationRequested)
            { Interlocked.Increment(ref _cancelledCount); return ExchangeOutcome.Failed("xsr.cancelled"); }
            if (timeoutSource.IsCancellationRequested)
            { Interlocked.Increment(ref _timedOutCount); return ExchangeOutcome.Failed("xsr.timed_out"); }
            return ExchangeOutcome.Failed("xsr.unavailable");
        }
        finally { lock (_gate) _pending.Remove(correlation.Value); }
    }

    private async ValueTask SendCancelAsync(SidecarCorrelationId correlation, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await SendTrackedAsync(new(SidecarProtocol.Version, SidecarMessageType.Cancel, SidecarFrameTraits.None,
            correlation, SidecarStateSnapshot.EncodeCancel(correlation.Value, reason)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { FailWithMirrorUnavailable("The sidecar cancellation could not be delivered."); }
    }

    private async Task CancelBestEffortAsync(Guid correlation, string reason)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await SendCancelAsync(new(correlation), reason, deadline.Token).ConfigureAwait(false);
    }

    private void CompleteExchange(SidecarFrame frame)
    {
        PendingExchange? exchange;
        lock (_gate)
        {
            if (!_pending.TryGetValue(frame.CorrelationId.Value, out exchange))
            { Interlocked.Increment(ref _lateResults); return; }
        }
        if (frame.MessageType != exchange.ExpectedResult)
            throw new SidecarProtocolException("Sidecar result message does not match its request.");
        var decoded = SidecarDataMessages.DecodeBinaryResultDetails(frame.Payload.Span);
        if (decoded.IsBinary && !NegotiatedFeatures.HasFlag(SidecarFeatures.BinaryPayloads))
            throw new SidecarProtocolException("Binary sidecar results were not negotiated.");
        if (decoded.Success && decoded.Value.CodecId != exchange.ResultCodecId)
            throw new SidecarProtocolException("Sidecar result codec does not match its declared contract.");
        string code = decoded.Success ? string.Empty : NormalizeRemoteError(decoded.ErrorCode);
        lock (_gate)
        {
            if (!_pending.Remove(frame.CorrelationId.Value)) return;
            Interlocked.Increment(ref _exchangesCompleted);
        }
        exchange.Completion.TrySetResult(new(decoded.Success, decoded.Value, code));
    }

    private void ApplyStateDelta(ReadOnlySpan<byte> payload)
    {
        lock (_gate) { if (!Accepting) return; }
        var decoded = SidecarDataMessages.DecodeStateDelta(payload);
        var entry = ResolveContract(SidecarRegistrationKind.State, decoded.ContractId);
        if (entry is null) return;
        lock (_mirrorGate)
        {
            lock (_gate) { if (!Accepting) return; }
            _mirror?.PublishFromWire(entry, decoded.EncodedValue);
            Interlocked.Increment(ref _stateDeltas);
        }
    }

    private void DeliverEvent(ReadOnlySpan<byte> payload)
    {
        lock (_gate) { if (!Accepting) return; }
        var decoded = SidecarDataMessages.DecodeBinaryEventDetails(payload);
        var entry = ResolveContract(SidecarRegistrationKind.Event, decoded.ContractId);
        if (entry is null) return;
        if (decoded.Value.CodecId != entry.CodecId || (decoded.IsBinary && !NegotiatedFeatures.HasFlag(SidecarFeatures.BinaryPayloads)))
            throw new SidecarProtocolException("Sidecar event codec does not match its contract.");
        lock (_gate) { if (!Accepting) return; }
        Interlocked.Increment(ref _eventsDelivered);
        try
        {
            Volatile.Read(ref _binaryEventObserver)?.OnEvent(entry.SemanticId, decoded.Value);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
        if (decoded.Value.CodecId == 0)
        {
            try { Volatile.Read(ref _eventObserver)?.OnEvent(entry.SemanticId, System.Text.Encoding.UTF8.GetString(decoded.Value.Span)); }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
        }
    }

    private void HandleRemoteFailure(SidecarFrame frame, bool crashed)
    {
        // Legacy CRASH has no payload; retain that peer contract.
        var failure = frame.Payload.IsEmpty ? (Code: "xsr.handler_faulted", Message: string.Empty)
            : SidecarControlMessages.DecodeFailure(frame.Payload.Span);
        string code = NormalizeRemoteError(failure.Code);
        lock (_gate) _lastRemoteError = code;
        if (crashed) { Interlocked.Increment(ref _crashes); FailWithMirrorUnavailable("The sidecar reported a crash."); }
        else
        {
            PendingExchange? exchange;
            PendingHealth? health;
            SidecarHostStream? stream;
            lock (_gate)
            {
                _pending.Remove(frame.CorrelationId.Value, out exchange);
                _healthPending.Remove(frame.CorrelationId.Value, out health);
                _streams.TryGetValue(frame.CorrelationId.Value, out stream);
            }
            if (exchange is not null) Interlocked.Increment(ref _exchangesCompleted);
            exchange?.Completion.TrySetResult(ExchangeOutcome.Failed(code));
            if (health is not null) RecordHealthOutcome(false);
            health?.Completion.TrySetResult(XsrResult.Failure<TimeSpan>(ExchangeError(code)));
            if (stream is not null) _ = ObserveBackgroundCleanupAsync(CancelStreamAsync(stream, code, notify: false, preserveCompleted: true).AsTask());
        }
    }

    private static string NormalizeRemoteError(string code) =>
        code.Length is > 0 and <= 128 && XsrSemanticId.TryParse(code, out _) ? code : "xsr.handler_faulted";

    private static XsrError ExchangeError(string code)
    {
        string valid = NormalizeRemoteError(code);
        var kind = valid switch
        {
            "xsr.cancelled" => XsrErrorKind.Cancelled,
            "xsr.timed_out" => XsrErrorKind.TimedOut,
            "xsr.unavailable" or "xsr.feature_unavailable" => XsrErrorKind.Unavailable,
            "xsr.backpressure" => XsrErrorKind.Backpressure,
            "xsr.contract_mismatch" => XsrErrorKind.ContractMismatch,
            "xsr.route_not_found" => XsrErrorKind.NotFound,
            "xsr.handler_faulted" => XsrErrorKind.Faulted,
            _ => XsrErrorKind.Rejected,
        };
        return new(kind, XsrSemanticId.Parse(valid), "The sidecar operation could not complete.");
    }

    private void FailWithMirrorUnavailable(string reason) { Fail(reason); InvalidateMirror(); }

    private void InvalidateMirror()
    {
        lock (_mirrorGate)
        {
            if (_mirror is not { } mirror || _registration is not { } registration) return;
            foreach (var entry in registration.Entries)
                if (entry.Kind == SidecarRegistrationKind.State && mirror.TryResolve(entry.SemanticId) is { } id)
                    mirror.Store.MarkAvailability(id, XsrStateAvailability.Unavailable);
        }
    }

    private void CloseAdmission() { lock (_gate) _admissionClosed = true; }

    private void OnActivated()
    {
        lock (_gate)
        {
            if (_stopped || _admissionClosed) return;
            _activationEnded = new();
            _activationAccepting = true;
        }
        lock (_mirrorGate)
        {
            lock (_gate) { if (!Accepting) return; }
            if (_mirror is not { } mirror || _registration is not { } registration) return;
            foreach (var entry in registration.Entries)
            {
                lock (_gate) { if (!Accepting) return; }
                if (entry.Kind == SidecarRegistrationKind.State && mirror.TryResolve(entry.SemanticId) is { } id)
                    mirror.Store.MarkAvailability(id, XsrStateAvailability.Available);
            }
        }
    }

    private void OnDeactivated()
    {
        KeyValuePair<Guid, PendingExchange>[] pending;
        CancellationTokenSource ended;
        lock (_gate)
        {
            _activationAccepting = false;
            ended = _activationEnded;
            pending = _pending.ToArray();
            _pending.Clear();
        }
        ended.Cancel();
        foreach (var (id, exchange) in pending)
        {
            exchange.Completion.TrySetResult(ExchangeOutcome.Failed("xsr.unavailable"));
            if (exchange.Sent && !Volatile.Read(ref _stopped)) _ = CancelBestEffortAsync(id, "sidecar deactivated");
        }
        EndHealthPings();
        EndStreams("xsr.unavailable");
        InvalidateMirror();
    }

    private void EndPending()
    {
        XsrUiPatchRuntime.CaptionLease? captions;
        XsrUiModuleRuntime.ModuleLease? modules;
        lock (_gate)
        {
            _stopped = true;
            _admissionClosed = true;
            _functionPatchLease?.Dispose(); _functionPatchLease = null; _functionPatches = [];
            _signalLease?.Dispose(); _signalLease = null; _signals = [];
            captions = _uiPatchLease; _uiPatchLease = null; _uiPatches = [];
            modules = _uiModuleLease; _uiModuleLease = null; _uiModules = [];
            Extensions = new();
        }
        captions?.Dispose(); modules?.Dispose();
        OnDeactivated();
        EndLifecycleControls();
        _sessionEnded.Cancel();
    }

    private sealed class PendingExchange(SidecarMessageType expectedResult, uint resultCodecId)
    {
        internal SidecarMessageType ExpectedResult { get; } = expectedResult;
        internal uint ResultCodecId { get; } = resultCodecId;
        internal volatile bool Sent;
        internal TaskCompletionSource<ExchangeOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record ExchangeOutcome(bool Success, SidecarBinaryValue? Value, string ErrorCode)
    { internal static ExchangeOutcome Failed(string code) => new(false, null, code); }
}

/// <summary>Compatibility outcome contract retained for existing callers.</summary>
public sealed record SidecarExchangeOutcome(bool Success, string Value, string ErrorCode)
{
    public static SidecarExchangeOutcome TimedOut() => new(false, string.Empty, "xsr.timed_out");
    public static SidecarExchangeOutcome Cancelled() => new(false, string.Empty, "xsr.cancelled");
    internal static SidecarExchangeOutcome Backpressure() => new(false, string.Empty, "xsr.backpressure");
}

public interface ISidecarSessionEventObserver { void OnEvent(XsrSemanticId SemanticId, string Payload); }
public interface ISidecarSessionBinaryEventObserver { void OnEvent(XsrSemanticId semanticId, SidecarBinaryValue value); }
