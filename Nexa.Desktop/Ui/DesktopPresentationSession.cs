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
    private readonly Action<bool> _setResizeEnabled;
    private int _pending = 1;
    private bool? _lastResizeEnabled;

    internal DesktopPresentationSession(XsrUiShell shell, XsrStateStore state, Action<bool> setResizeEnabled)
    {
        _shell = shell;
        _state = state;
        _setResizeEnabled = setResizeEnabled;
        state.TryResolve(XsrSemanticId.Parse("SystemDisableUiAnimations"), out _animationsDisabled);
        state.TryResolve(XsrSemanticId.Parse("UiLockWindowSize"), out _windowLocked);
        state.Changed += OnChanged;
        shell.Renderer.FramePreparing += OnFrame;
        OnFrame(this, EventArgs.Empty);
    }

    private void OnChanged(XsrStateChange change)
    {
        if (change.Id == _animationsDisabled || change.Id == _windowLocked)
            Interlocked.Exchange(ref _pending, 1);
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _pending, 0) == 0) return;
        _shell.Renderer.ReducedMotion = _animationsDisabled.IsAssigned && _state.Read<bool>(_animationsDisabled).Value;
        bool resizeEnabled = !_windowLocked.IsAssigned || !_state.Read<bool>(_windowLocked).Value;
        if (_lastResizeEnabled == resizeEnabled) return;
        _lastResizeEnabled = resizeEnabled;
        _setResizeEnabled(resizeEnabled);
    }

    public void Dispose()
    {
        _state.Changed -= OnChanged;
        _shell.Renderer.FramePreparing -= OnFrame;
    }
}
