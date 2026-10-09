using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed record AvaloniaUiDesktopPolicy(bool TrayEnabled = true, bool CloseToTray = false, bool MinimizeToTray = false);

/// <summary>Native desktop affordances; no account, service or renderer-tree access.</summary>
internal sealed class AvaloniaUiDesktopIntegration : IDisposable
{
    private readonly AvaloniaUiShellWindow _window;
    private readonly Action _restore;
    private readonly Action _settings;
    private readonly Func<string, string> _localize;
    private readonly Func<TrayIcon?> _createTray;
    private TrayIcon? _tray;
    private AvaloniaUiDesktopPolicy _policy = new(false);
    private bool _disposed;
    private bool _hostAvailable = !OperatingSystem.IsLinux();
    private CancellationTokenSource? _availabilityStop;

    internal AvaloniaUiDesktopIntegration(AvaloniaUiShellWindow window, Action restore, Action settings, Func<string, string> localize)
        : this(window, restore, settings, localize, CreateNativeTray) { }

    internal AvaloniaUiDesktopIntegration(AvaloniaUiShellWindow window, Action restore, Action settings,
        Func<string, string> localize, Func<TrayIcon?> createTray)
    {
        _window = window; _restore = restore; _settings = settings; _localize = localize;
        _createTray = createTray ?? throw new ArgumentNullException(nameof(createTray));
        window.HideToTrayRequested = TryHide;
        window.MinimizeToTrayRequested = TryMinimizeToTray;
        window.PropertyChanged += OnChanged;
        window.Closed += OnClosed;
    }

    internal bool TrayAvailable => _tray is not null && _hostAvailable;

    internal void Apply(AvaloniaUiDesktopPolicy policy)
    {
        if (_disposed) return;
        _policy = policy;
        if (!policy.TrayEnabled)
        {
            _hostAvailable = !OperatingSystem.IsLinux();
            _tray?.Dispose(); _tray = null;
            _availabilityStop?.Cancel(); _availabilityStop?.Dispose(); _availabilityStop = null;
            if (!_window.IsVisible || _window.IsHidingToTray) _restore();
            return;
        }
        if (_tray is not null || _window.Icon is null) return;
        TrayIcon? tray = _createTray();
        if (tray is null) return;
        NativeMenu menu = new();
        NativeMenuItem restore = new(_localize("显示主窗口"));
        restore.Click += (_, _) => _restore();
        NativeMenuItem settings = new(_localize("设置"));
        settings.Click += (_, _) => { _restore(); _settings(); };
        NativeMenuItem exit = new(_localize("退出"));
        exit.Click += (_, _) => _window.RequestClose();
        menu.Items.Add(restore); menu.Items.Add(settings); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(exit);
        _tray = tray;
        _tray.Icon = _window.Icon; _tray.ToolTipText = _window.Title; _tray.Menu = menu; _tray.IsVisible = true;
        _tray.Clicked += (_, _) => _restore();
        if (OperatingSystem.IsLinux())
        {
            _availabilityStop = new();
            CancellationToken token = _availabilityStop.Token;
            _ = MonitorHostAsync(token);
        }
    }

    private static TrayIcon? CreateNativeTray()
    {
        using ITrayIconImpl? probe = PlatformManager.CreateTrayIcon();
        return probe is null ? null : new TrayIcon();
    }

    private bool TryHide()
    {
        if (!_policy.CloseToTray || !_policy.TrayEnabled) return false;
        if (TrayAvailable) _window.HideToTray();
        else _window.WindowState = WindowState.Minimized;
        return true;
    }
    private bool TryMinimizeToTray()
    {
        if (!_policy.MinimizeToTray || !TrayAvailable) return false;
        _window.HideToTray();
        return true;
    }
    private void OnChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Window.IconProperty && _tray is not null) _tray.Icon = _window.Icon;
        if (args.Property == Window.WindowStateProperty && _window.WindowState == WindowState.Minimized
            && _policy.MinimizeToTray && TrayAvailable) _window.HideToTray();
    }
    private void OnClosed(object? sender, EventArgs args) => Dispose();
    private async Task MonitorHostAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool available = await Task.Run(() => AvaloniaTrayAvailability.IsAvailableAsync(token), token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || token.IsCancellationRequested) return;
                    _hostAvailable = available;
                    if (!available && (!_window.IsVisible || _window.IsHidingToTray)) _restore();
                });
                await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.HideToTrayRequested = null;
        _window.MinimizeToTrayRequested = null;
        _window.PropertyChanged -= OnChanged;
        _window.Closed -= OnClosed;
        _tray?.Dispose(); _tray = null;
        _availabilityStop?.Cancel(); _availabilityStop?.Dispose(); _availabilityStop = null;
        if (_window.IsHidingToTray) _restore();
    }
}

public sealed partial class AvaloniaUiPlatformActions
{
    private AvaloniaUiDesktopPolicy? _desktopPolicy;
    private AvaloniaUiDesktopIntegration? _desktopIntegration;
    private readonly object _postedGate = new();
    private readonly Queue<Action> _beforeAttach = new();
    public Action? OpenSettingsRequested { get; set; }
    public event Action<string>? ProtocolActivated;
    public event Action? WindowClosed;
    private bool _windowClosed;
    private void OnDesktopWindowClosed(object? sender, EventArgs args)
    {
        // An abandoned hidden attempt is not an application close; single-instance ownership
        // and activation forwarding survive so the startup window can retry composition.
        if (_startupHidden)
        {
            _desktopIntegration?.Dispose();
            _desktopIntegration = null;
            _owner = null;
            lock (_postedGate) { _windowClosed = true; _beforeAttach.Clear(); }
            return;
        }
        _clipboardScreenshot?.Dispose();
        _clipboardScreenshot = null;
        lock (_postedGate) { _windowClosed = true; _beforeAttach.Clear(); }
        WindowClosed?.Invoke();
    }
    internal void OnProtocolActivated(string uri) => ProtocolActivated?.Invoke(uri);
    public Func<string, string>? LocalizeDesktopText { get; set; }

    public void PostToWindow(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_postedGate)
        {
            if (_windowClosed) return;
            if (_owner is null)
            {
                if (_beforeAttach.Count < 16) _beforeAttach.Enqueue(action);
                return;
            }
        }
        Dispatcher.UIThread.Post(action);
    }
    public void SetDesktopPolicy(AvaloniaUiDesktopPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _desktopPolicy = policy;
        if (_owner is not null) PostToWindow(() => _desktopIntegration?.Apply(_desktopPolicy));
    }
    private void AttachDesktopIntegration(TopLevel owner)
    {
        if (owner is Window lifetimeWindow) lifetimeWindow.Closed += OnDesktopWindowClosed;
        if (owner is AvaloniaUiShellWindow window && _desktopPolicy is not null)
        {
            _desktopIntegration = new(window, RestoreWindow, () => OpenSettingsRequested?.Invoke(),
                text => LocalizeDesktopText?.Invoke(text) ?? text);
            _desktopIntegration.Apply(_desktopPolicy);
        }
        lock (_postedGate)
            while (_beforeAttach.TryDequeue(out Action? action)) Dispatcher.UIThread.Post(action);
    }
}
