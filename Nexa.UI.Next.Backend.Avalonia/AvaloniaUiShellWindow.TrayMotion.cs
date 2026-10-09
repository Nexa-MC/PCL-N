using Avalonia.Controls;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiShellWindow
{
    private bool? _trayVisibilityTarget;
    private bool _showingForTrayRestore;
    private WindowState _lastNonMinimizedState = WindowState.Normal;

    internal bool IsHidingToTray => _trayVisibilityTarget == false && IsVisible;
    internal Func<bool>? MinimizeToTrayRequested { get; set; }

    internal void RequestMinimize()
    {
        if (_disposed || _closeAnimationStarted) return;
        if (MinimizeToTrayRequested?.Invoke() == true) return;
        WindowState = WindowState.Minimized;
    }

    internal void HideToTray()
    {
        if (_disposed || _closeAnimationStarted || !IsVisible || _trayVisibilityTarget == false) return;
        double presented = _maskedContent.Opacity;
        _trayVisibilityTarget = false;
        if (!_startupEntranceCompleted)
        {
            // A visibility command retires first-show decoration, rather than allowing it
            // to overwrite the same content opacity while a tray transition runs.
            OnStartupRevealCompleted();
            if (_disposed || _closeAnimationStarted || _trayVisibilityTarget != false) return;
            _maskedContent.Opacity = presented;
        }
        AnimateTrayVisibility(0, AvaloniaMotionTokens.TrayHideMilliseconds,
            immediate: WindowState == WindowState.Minimized);
    }

    internal void RestoreFromTray(WindowState? minimizedState = null)
    {
        if (_disposed || _closeAnimationStarted) return;
        if (!_hasOpened)
        {
            Show(); Activate();
            return;
        }
        bool restoring = !IsVisible || _trayVisibilityTarget == false;
        if (restoring)
        {
            AvaloniaUiMotion.Cancel(this, "tray-visibility");
            _trayVisibilityTarget = true;
            // Prepare the transparent retained subtree before native Show. Window.Opacity
            // stays at one, preserving composited Win32 capabilities and input coordinates.
            if (!IsVisible) _maskedContent.Opacity = 0;
        }
        if (WindowState == WindowState.Minimized)
            WindowState = minimizedState ?? _lastNonMinimizedState;
        if (!IsVisible)
        {
            _showingForTrayRestore = true;
            try { Show(); }
            finally { _showingForTrayRestore = false; }
        }
        Activate();
        if (_disposed || _closeAnimationStarted) return;
        if (restoring && _trayVisibilityTarget == true)
            AnimateTrayVisibility(1, AvaloniaMotionTokens.TrayRestoreMilliseconds);
    }

    private void AnimateTrayVisibility(double target, int milliseconds, bool immediate = false)
    {
        AvaloniaUiMotion.Animate(this, "tray-visibility", () => _maskedContent.Opacity,
            value => _maskedContent.Opacity = value, target,
            immediate || _shell.Renderer.ReducedMotion ? 0 : milliseconds,
            easing: target == 0 ? AvaloniaUiMotion.EaseIn : AvaloniaUiMotion.EaseOut,
            completed: () =>
            {
                if (_disposed || _closeAnimationStarted || _trayVisibilityTarget != (target == 1)) return;
                if (target == 0) Hide();
                else _trayVisibilityTarget = null;
            },
            // Hidden/inactive activity suspends optional scene motion. This user-triggered
            // finite transition follows only the explicit preference, including reversals.
            reducedMotion: () => _shell.Renderer.ReducedMotion);
    }

    private void CancelTrayVisibility()
    {
        AvaloniaUiMotion.Cancel(this, "tray-visibility");
        _trayVisibilityTarget = null;
        _maskedContent.Opacity = 1;
    }
}
