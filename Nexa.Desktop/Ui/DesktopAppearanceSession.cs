using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>Applies only committed preferences at the frame boundary, including while Settings is closed.</summary>
internal sealed class DesktopAppearanceSession : IDisposable
{
    private readonly XsrUiShell _shell;
    private readonly XsrStateStore _state;
    private readonly XsrStateId _mode, _accent;
    private readonly IXsrUiSystemAppearance? _system;
    private readonly XsrUiColorScheme _previousScheme;
    private XsrUiThemeMode? _previousMode;
    private int _pending = 1;
    private bool _disposed;

    internal DesktopAppearanceSession(XsrUiShell shell, XsrStateStore state, IXsrUiSystemAppearance? system = null)
    {
        _shell = shell;
        _state = state;
        _system = system;
        _previousScheme = shell.Renderer.ColorScheme;
        state.TryResolve(XsrSemanticId.Parse("UiDarkMode"), out _mode);
        state.TryResolve(XsrSemanticId.Parse("UiAccentColor"), out _accent);
        state.Changed += OnChanged;
        if (_system is not null) _system.SystemAppearanceChanged += OnSystemChanged;
        shell.Renderer.FramePreparing += OnFrame;
        OnFrame(this, EventArgs.Empty);
    }

    private void OnChanged(XsrStateChange change)
    {
        if (change.Id == _mode || change.Id == _accent) Interlocked.Exchange(ref _pending, 1);
    }
    private void OnSystemChanged() => Interlocked.Exchange(ref _pending, 1);

    private void OnFrame(object? sender, EventArgs args)
    {
        if (_disposed || Interlocked.Exchange(ref _pending, 0) == 0) return;
        XsrUiThemeMode mode = _mode.IsAssigned ? _state.Read<int>(_mode).Value switch
        { 0 => XsrUiThemeMode.Light, 1 => XsrUiThemeMode.Dark, _ => XsrUiThemeMode.System } : XsrUiThemeMode.System;
        XsrUiAccent accent = _accent.IsAssigned ? _state.Read<string>(_accent).Value switch
        { "purple" => XsrUiAccent.Purple, "green" => XsrUiAccent.Green, "orange" => XsrUiAccent.Orange, _ => XsrUiAccent.Blue } : XsrUiAccent.Blue;
        bool dark = mode == XsrUiThemeMode.Dark || mode == XsrUiThemeMode.System && _system?.IsSystemDark == true;
        _shell.Renderer.ColorScheme = new(dark, accent);
        if (_previousMode == mode) return;
        _previousMode = mode;
        _system?.SetThemeMode(mode);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.Changed -= OnChanged;
        if (_system is not null) _system.SystemAppearanceChanged -= OnSystemChanged;
        _shell.Renderer.FramePreparing -= OnFrame;
        _shell.Renderer.ColorScheme = _previousScheme;
    }
}
