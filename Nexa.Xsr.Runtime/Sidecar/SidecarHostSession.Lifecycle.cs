using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;

namespace Nexa.Xsr.Runtime;

public sealed partial class SidecarHostSession
{
    private Task? _shutdownTask;
    private UnregistrationControl? _unregistrationControl;

    /// <summary>
    /// Explicitly retires the registered session. This operation requires negotiated
    /// unregistration and completes through the session's existing receive loop.
    /// </summary>
    public async ValueTask UnregisterAsync(
        string reason = "Host requested unregistration.",
        CancellationToken cancellationToken = default)
    {
        if ((NegotiatedFeatures & SidecarFeatures.Unregistration) == 0)
            throw new InvalidOperationException("The peer did not negotiate unregistration.");
        byte[] payload = SidecarControlMessages.EncodeUnregister(reason);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == SidecarSessionState.Closed) return;
            if (State is not (SidecarSessionState.Active or SidecarSessionState.Ready))
                throw new InvalidOperationException("Unregistration requires a registered, ready session.");
            CloseAdmission();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionEnded.Token);
            deadline.CancelAfter(_limits.ShutdownTimeout);
            try
            {
                if (State == SidecarSessionState.Active)
                    await DeactivateCoreAsync(deadline.Token).ConfigureAwait(false);
                else { OnDeactivated(); RetireActivationLeases(); }
                await SendUnregistrationAsync(payload, deadline.Token).ConfigureAwait(false);
            }
            finally { EndPending(); Transition(SidecarSessionState.Closed); _connection.Close(); }
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        // Defer the body until the shared task has been published under _gate. Observers may
        // reenter ShutdownAsync and must always receive that same task.
        await Task.Yield();
        CloseAdmission();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_limits.ShutdownTimeout);
        bool entered = false;
        try
        {
            await _lifecycleGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            if (_connection.State != SidecarConnectionState.Connected || State == SidecarSessionState.Closed) return;
            if (State == SidecarSessionState.Active)
                await DeactivateCoreAsync(deadline.Token).ConfigureAwait(false);
            else { OnDeactivated(); RetireActivationLeases(); }

            if ((NegotiatedFeatures & SidecarFeatures.Unregistration) != 0
                && State == SidecarSessionState.Ready)
            {
                // Reserve part of the overall deadline for SHUTDOWN if a peer does not
                // acknowledge UNREGISTER. Waiting never introduces a second frame reader.
                using var acknowledgement = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                acknowledgement.CancelAfter(TimeSpan.FromTicks(Math.Max(1, _limits.ShutdownTimeout.Ticks / 2)));
                try
                {
                    await SendUnregistrationAsync(
                        SidecarControlMessages.EncodeUnregister("Host shutdown."), acknowledgement.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            }
            await SendTrackedAsync(new SidecarFrame(SidecarProtocol.Version, SidecarMessageType.Shutdown,
                SidecarFrameTraits.Final, SidecarCorrelationId.Create(), Array.Empty<byte>()), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            // A disconnected, failed or unresponsive peer cannot prevent authoritative local
            // retirement. Cancellation bounds normal teardown instead of leaving live leases.
        }
        finally
        {
            EndPending();
            Transition(SidecarSessionState.Closed);
            _connection.Close();
            if (entered) _lifecycleGate.Release();
        }
    }

    private async ValueTask DeactivateCoreAsync(CancellationToken cancellationToken)
    {
        OnDeactivated();
        RetireActivationLeases();
        await SendTrackedAsync(new SidecarFrame(SidecarProtocol.Version, SidecarMessageType.Deactivate,
            SidecarFrameTraits.None, SidecarCorrelationId.Create(), Array.Empty<byte>()), cancellationToken).ConfigureAwait(false);
        Transition(SidecarSessionState.Ready);
    }

    private void RetireActivationLeases()
    {
        IDisposable? functionLease;
        XsrSignalRuntime.SignalLease? signalLease;
        XsrUiPatchRuntime.CaptionLease? captionLease;
        XsrUiModuleRuntime.ModuleLease? moduleLease;
        lock (_gate)
        {
            functionLease = _functionPatchLease;
            _functionPatchLease = null;
            signalLease = _signalLease;
            _signalLease = null;
            captionLease = _uiPatchLease;
            _uiPatchLease = null;
            moduleLease = _uiModuleLease;
            _uiModuleLease = null;
        }
        functionLease?.Dispose();
        signalLease?.Dispose();
        captionLease?.Dispose();
        moduleLease?.Dispose();
    }

    private async ValueTask SendUnregistrationAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var control = new UnregistrationControl(SidecarCorrelationId.Create());
        lock (_gate)
        {
            if (_stopped) throw new SidecarProtocolException("The session ended before unregistration.");
            if (_unregistrationControl is not null) throw new InvalidOperationException("Unregistration is already pending.");
            _unregistrationControl = control;
        }
        try
        {
            await SendTrackedAsync(new SidecarFrame(SidecarProtocol.Version, SidecarMessageType.Unregister,
                SidecarFrameTraits.None, control.Correlation, payload), cancellationToken).ConfigureAwait(false);
            if (!await control.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false))
                throw new SidecarProtocolException("The session ended without an unregistration acknowledgement.");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_unregistrationControl, control)) _unregistrationControl = null;
            }
        }
    }

    private async ValueTask<bool> HandleLifecycleControlAsync(SidecarFrame frame, CancellationToken cancellationToken)
    {
        if (frame.MessageType is not (SidecarMessageType.Unregister or SidecarMessageType.Unregistered)) return false;
        if ((NegotiatedFeatures & SidecarFeatures.Unregistration) == 0)
            throw Fail("The peer sent an unnegotiated unregistration control.");
        if (frame.MessageType == SidecarMessageType.Unregistered)
        {
            UnregistrationControl? control;
            lock (_gate) control = _unregistrationControl;
            // Late and unrelated replies are discarded before payload decoding.
            if (control is null || control.Correlation != frame.CorrelationId) return true;
            _ = SidecarControlMessages.DecodeUnregister(frame.Payload.Span);
            control.Completion.TrySetResult(true);
            return true;
        }
        string reason = SidecarControlMessages.DecodeUnregister(frame.Payload.Span);
        CloseAdmission();
        OnDeactivated();
        RetireActivationLeases();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_limits.ShutdownTimeout);
        try
        {
            await SendTrackedAsync(new SidecarFrame(SidecarProtocol.Version, SidecarMessageType.Unregistered,
                SidecarFrameTraits.Final, frame.CorrelationId, SidecarControlMessages.EncodeUnregister(reason)),
                deadline.Token).ConfigureAwait(false);
        }
        finally { EndPending(); Transition(SidecarSessionState.Closed); _connection.Close(); }
        return true;
    }

    private void EndLifecycleControls()
    {
        UnregistrationControl? control;
        lock (_gate) { control = _unregistrationControl; _unregistrationControl = null; }
        control?.Completion.TrySetResult(false);
    }

    private sealed class UnregistrationControl(SidecarCorrelationId correlation)
    {
        internal SidecarCorrelationId Correlation { get; } = correlation;
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
