using System.Threading.Channels;
using Nexa.Services.Accounts;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Settings;
using Nexa.Services.Tasks;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>Projects committed settings on the render thread, independently of settings navigation.</summary>
internal sealed class DesktopPresentationSession : IDisposable
{
    private readonly XsrUiShell _shell;
    private readonly XsrStateStore _state;
    private readonly XsrStateId _animationsDisabled;
    private readonly XsrStateId _windowLocked;
    private readonly XsrStateId _animationFrameRate;
    private readonly XsrStateId _lowPower, _activity, _tasks, _launch, _login;
    private readonly Action<bool> _setResizeEnabled;
    private readonly Action<int>? _setAnimationFrameRate;
    private readonly XsrQueryRouter? _queries;
    private readonly Action<Action>? _postToRender;
    private readonly XsrStateId _policyRevision;
    private readonly CancellationTokenSource _policyStop = new();
    private readonly Channel<XsrStateChange> _policyUpdates = Channel.CreateBounded<XsrStateChange>(
        new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _policyWorker;
    private int _preferenceReducedMotion, _disposed;
    private int _pending = 1;
    private bool? _lastResizeEnabled;
    private int? _lastFrameRate;

    internal DesktopPresentationSession(XsrUiShell shell, XsrStateStore state, Action<bool> setResizeEnabled,
        Action<int>? setAnimationFrameRate = null, XsrQueryRouter? queries = null, Action<Action>? postToRender = null)
    {
        _shell = shell;
        _state = state;
        _setResizeEnabled = setResizeEnabled;
        _setAnimationFrameRate = setAnimationFrameRate;
        _queries = queries; _postToRender = postToRender;
        if (queries is not null) state.TryResolve(SettingsPolicyContract.RevisionKey, out _policyRevision);
        state.TryResolve(XsrSemanticId.Parse("SystemDisableUiAnimations"), out _animationsDisabled);
        state.TryResolve(XsrSemanticId.Parse("UiLockWindowSize"), out _windowLocked);
        state.TryResolve(XsrSemanticId.Parse("UiAniFPS"), out _animationFrameRate);
        state.TryResolve(XsrSemanticId.Parse("UiUltraLowPowerMode"), out _lowPower);
        state.TryResolve(XsrUiShellWindowState.Activity, out _activity);
        state.TryResolve(TaskCenterStateContract.SummaryKey, out _tasks);
        state.TryResolve(MinecraftLaunchProgressState.SnapshotKey, out _launch);
        state.TryResolve(AccountOnboardingState.Login, out _login);
        state.Changed += OnChanged;
        shell.Renderer.FramePreparing += OnFrame;
        OnFrame(this, EventArgs.Empty);
        _policyWorker = queries is null ? Task.CompletedTask : ReadReducedMotionAsync(_policyStop.Token);
        if (queries is not null) _policyUpdates.Writer.TryWrite(default);
    }

    private void OnChanged(XsrStateChange change)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (change.Id == _policyRevision && _queries is not null) _policyUpdates.Writer.TryWrite(change);
        if (change.Id == _animationsDisabled || change.Id == _windowLocked || change.Id == _animationFrameRate
            || change.Id == _lowPower || change.Id == _activity || change.Id == _tasks || change.Id == _launch || change.Id == _login)
            Interlocked.Exchange(ref _pending, 1);
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _pending, 0) == 0) return;
        _shell.Renderer.ReducedMotion = Volatile.Read(ref _preferenceReducedMotion) != 0
            || _animationsDisabled.IsAssigned && _state.Read<bool>(_animationsDisabled).Value;
        bool resizeEnabled = !_windowLocked.IsAssigned || !_state.Read<bool>(_windowLocked).Value;
        if (_lastResizeEnabled != resizeEnabled)
        {
            _lastResizeEnabled = resizeEnabled;
            _setResizeEnabled(resizeEnabled);
        }
        int frameRate = _animationFrameRate.IsAssigned ? (int)Math.Clamp((long)_state.Read<int>(_animationFrameRate).Value + 1, 1, 240) : 60;
        if (CanUseLowPower()) frameRate = Math.Min(frameRate, 10);
        if (_lastFrameRate == frameRate) return;
        _lastFrameRate = frameRate;
        _setAnimationFrameRate?.Invoke(frameRate);
    }

    private async Task ReadReducedMotionAsync(CancellationToken token)
    {
        try
        {
            await foreach (XsrStateChange change in _policyUpdates.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var settings = await CommittedSettingsRead.QueryAsync(_queries!, token).ConfigureAwait(false);
                if (settings is null || Volatile.Read(ref _disposed) != 0) continue;
                bool reduced = settings.Values.Any(item => item.Key == "appearance.reduced-motion"
                    && bool.TryParse(item.Value.Value, out bool enabled) && enabled);
                int next = reduced ? 1 : 0;
                if (Interlocked.Exchange(ref _preferenceReducedMotion, next) == next) continue;
                Interlocked.Exchange(ref _pending, 1);
                // Re-queue the actual committed revision only to request a new frame; the bridge never mutates the tree here.
                if (change.Id.IsAssigned) _shell.StateBridge?.OnChanged(change);
                _postToRender?.Invoke(() =>
                {
                    if (Volatile.Read(ref _disposed) == 0 && _shell.Tree.IsAlive(_shell.Root))
                        _shell.Tree.MarkDirty(_shell.Root, XsrUiDirtyKinds.Paint);
                });
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool CanUseLowPower() => _lowPower.IsAssigned && _state.Read<bool>(_lowPower).Value
        && _activity.IsAssigned && _state.ReadAppliedValue(_activity) is XsrUiWindowActivity activity
        && (!activity.IsActive || activity.IsMinimized)
        && (!_tasks.IsAssigned || _state.ReadAppliedValue(_tasks) is not TaskCenterSummary { ActiveCount: > 0 })
        && (!_launch.IsAssigned || _state.ReadAppliedValue(_launch) is not MinecraftLaunchProgressSnapshot { Active: true, IsLaunched: false })
        && (!_login.IsAssigned || _state.ReadAppliedValue(_login) is not AccountLoginSnapshot { IsBusy: true });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _state.Changed -= OnChanged;
        _shell.Renderer.FramePreparing -= OnFrame;
        _policyUpdates.Writer.TryComplete(); _policyStop.Cancel();
        _ = ReleasePolicyAsync();
    }

    private async Task ReleasePolicyAsync()
    {
        try { await _policyWorker.ConfigureAwait(false); }
        finally { _policyStop.Dispose(); }
    }
}
