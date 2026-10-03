using Nexa.UI.Next;
using Nexa.Xsr;
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
    private readonly Action<bool> _setResizeEnabled;
    private readonly Action<int>? _setAnimationFrameRate;
    private int _pending = 1;
    private bool? _lastResizeEnabled;
    private int? _lastFrameRate;

    internal DesktopPresentationSession(XsrUiShell shell, XsrStateStore state, Action<bool> setResizeEnabled, Action<int>? setAnimationFrameRate = null)
    {
        _shell = shell;
        _state = state;
        _setResizeEnabled = setResizeEnabled;
        _setAnimationFrameRate = setAnimationFrameRate;
        state.TryResolve(XsrSemanticId.Parse("SystemDisableUiAnimations"), out _animationsDisabled);
        state.TryResolve(XsrSemanticId.Parse("UiLockWindowSize"), out _windowLocked);
        state.TryResolve(XsrSemanticId.Parse("UiAniFPS"), out _animationFrameRate);
        state.Changed += OnChanged;
        shell.Renderer.FramePreparing += OnFrame;
        OnFrame(this, EventArgs.Empty);
    }

    private void OnChanged(XsrStateChange change)
    {
        if (change.Id == _animationsDisabled || change.Id == _windowLocked || change.Id == _animationFrameRate)
            Interlocked.Exchange(ref _pending, 1);
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _pending, 0) == 0) return;
        _shell.Renderer.ReducedMotion = _animationsDisabled.IsAssigned && _state.Read<bool>(_animationsDisabled).Value;
        bool resizeEnabled = !_windowLocked.IsAssigned || !_state.Read<bool>(_windowLocked).Value;
        if (_lastResizeEnabled != resizeEnabled)
        {
            _lastResizeEnabled = resizeEnabled;
            _setResizeEnabled(resizeEnabled);
        }
        int frameRate = _animationFrameRate.IsAssigned ? (int)Math.Clamp((long)_state.Read<int>(_animationFrameRate).Value + 1, 1, 240) : 60;
        if (_lastFrameRate == frameRate) return;
        _lastFrameRate = frameRate;
        _setAnimationFrameRate?.Invoke(frameRate);
    }

    public void Dispose()
    {
        _state.Changed -= OnChanged;
        _shell.Renderer.FramePreparing -= OnFrame;
    }
}
