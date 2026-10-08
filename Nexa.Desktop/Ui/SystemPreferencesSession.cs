using System.Globalization;
using System.Threading.Channels;
using Nexa.Services.Settings;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>Committed preferences own registrations, never unsaved form drafts.</summary>
internal sealed class SystemPreferencesSession : IAsyncDisposable
{
    private readonly XsrQueryRouter _queries;
    private readonly XsrStateStore _state;
    private readonly XsrStateId _revision;
    private readonly AvaloniaUiPlatformActions _platform;
    private readonly DesktopFeedbackService _feedback;
    private readonly Action<string> _activateProtocol;
    private readonly Action<string> _log;
    private readonly Action<DesktopDestination>? _startupNavigate;
    private readonly Action<bool>? _setLaunchHints;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Func<CancellationToken, Task>> _work = Channel.CreateBounded<Func<CancellationToken, Task>>(new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _worker;
    private readonly Channel<bool> _policy = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _policyWorker;
    private readonly TaskCompletionSource _initialReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task InitialReady => _initialReady.Task;
    private readonly HashSet<Guid> _notified = [];
    private bool? _autostart, _association, _jumpList;
    private volatile bool _nativeNotifications, _notificationActions, _clipboard;
    private string? _lastClipboard;
    private int _readingClipboard;
    private bool _disposed;
    private bool _startupApplied;

    internal SystemPreferencesSession(XsrQueryRouter queries, XsrStateStore state,
        AvaloniaUiPlatformActions platform, DesktopFeedbackService feedback, Action<string> activateProtocol, Action<string> log,
        Action<DesktopDestination>? startupNavigate = null, Action<bool>? setLaunchHints = null)
    {
        _queries = queries; _state = state; _platform = platform; _feedback = feedback;
        _activateProtocol = activateProtocol; _log = log;
        _startupNavigate = startupNavigate; _setLaunchHints = setLaunchHints;
        _revision = state.Resolve(SettingsPolicyContract.RevisionKey);
        _worker = RunAsync();
        _policyWorker = RunPolicyAsync();
        _state.Changed += OnState;
        _platform.WindowActivated += OnActivated;
        _feedback.Changed += OnFeedback;
        foreach (var note in feedback.Snapshot().Notifications) _notified.Add(note.Id);
        _policy.Writer.TryWrite(true);
    }

    private void OnState(XsrStateChange change) { if (change.Id == _revision) _policy.Writer.TryWrite(true); }
    private void ApplyPolicy(IReadOnlyDictionary<string, string?> values)
    {
        if (_disposed) return;
        bool Read(string key, bool fallback) => bool.TryParse(values.GetValueOrDefault(key), out bool value) ? value : fallback;
        _nativeNotifications = Read("general.native-notifications", true);
        _notificationActions = Read("general.notification-actions", true);
        _clipboard = Read("general.clipboard-detection", false);
        int opacity = int.TryParse(values.GetValueOrDefault("appearance.window-opacity"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? Math.Clamp(parsed, 40, 100) : 100;
        bool blur = Read("appearance.window-blur", false);
        bool hints = Read("general.launch-hints", true);
        _platform.PostToWindow(() =>
        { if (!_disposed) { _platform.SetWindowAppearance(opacity, blur); _setLaunchHints?.Invoke(hints); } });
        if (!_startupApplied)
        {
            _startupApplied = true;
            DesktopDestination destination = StartupDestination(values.GetValueOrDefault("general.startup-page"));
            _platform.PostToWindow(() => { if (!_disposed) _startupNavigate?.Invoke(destination); });
        }
    }
    internal static DesktopDestination StartupDestination(string? value) => value switch
    {
        "install" => DesktopDestination.Install,
        "resources" => DesktopDestination.Resources,
        "settings" => DesktopDestination.Settings,
        "java" => DesktopDestination.Java,
        "storage" => DesktopDestination.Storage,
        "about" => DesktopDestination.About,
        "tasks" => DesktopDestination.Tasks,
        _ => DesktopDestination.Launch,
    };

    private void OnFeedback(object? sender, EventArgs args)
    {
        if (_disposed) return;
        var notes = _feedback.Snapshot().Notifications;
        lock (_notified)
        {
            _notified.IntersectWith(notes.Select(note => note.Id));
            foreach (var note in notes)
                if (_notified.Add(note.Id) && _nativeNotifications)
                    _work.Writer.TryWrite(async token =>
                    {
                        if (!_nativeNotifications) return;
                        bool delivered;
                        if (_notificationActions) delivered = await _platform.NotifyNativeWithActionAsync("NexaCL", note.Message,
                            () => { if (!_disposed && _nativeNotifications && _notificationActions) { _platform.RestoreWindow(); _activateProtocol("nexacl://launch"); } }, token).ConfigureAwait(false);
                        else delivered = await _platform.NotifyNativeAsync("NexaCL", note.Message, token).ConfigureAwait(false);
                        if (!delivered && !token.IsCancellationRequested) _log("原生通知：DependencyMissing；启动器内通知仍可读取。");
                    });
        }
    }

    private void OnActivated()
    {
        if (_disposed || !_clipboard || Interlocked.CompareExchange(ref _readingClipboard, 1, 0) != 0) return;
        _ = DetectClipboardAsync();
    }
    private async Task DetectClipboardAsync()
    {
        try
        {
            string? text = await _platform.ReadClipboardTextAsync(_stop.Token).ConfigureAwait(false);
            if (_disposed || !_clipboard || text is null || text == _lastClipboard || !DesktopActivation.TryParse(text, out _)) return;
            _lastClipboard = text;
            _platform.PostToWindow(() =>
            {
                if (_disposed || !_clipboard || _feedback.Snapshot().Dialog is not null) return;
                _feedback.ShowDialog("clipboard.nexacl", "检测到 NexaCL 链接", "是否打开剪贴板中的 NexaCL 页面链接？", "打开", "忽略",
                    accepted => { if (accepted && !_disposed && _clipboard) _activateProtocol(text); });
            });
        }
        catch (Exception failure) when (failure is InvalidOperationException or IOException or OperationCanceledException)
        { if (!_stop.IsCancellationRequested) _log("剪贴板检测不可用：" + failure.GetType().Name); }
        finally { Interlocked.Exchange(ref _readingClipboard, 0); }
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var action in _work.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                try { await action(_stop.Token).ConfigureAwait(false); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException or PlatformNotSupportedException)
                { if (!_stop.IsCancellationRequested) _log("系统偏好应用失败：" + failure.Message); }
        }
        catch (OperationCanceledException) { }
    }
    private async Task RunPolicyAsync()
    {
        try
        {
            await foreach (bool ignored in _policy.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                // Latest committed policy is re-read after every native effect; notifications cannot fill this queue.
                var result = await CommittedSettingsRead.QueryAsync(_queries, _stop.Token).ConfigureAwait(false);
                if (result is null) { _initialReady.TrySetException(new IOException("无法读取初始系统设置。")); continue; }
                var values = result.Values.ToDictionary(item => item.Key, item => item.Value.Value);
                ApplyPolicy(values);
                bool Read(string key) => bool.TryParse(values.GetValueOrDefault(key), out bool value) && value;
                bool autostart = Read("general.autostart"), association = Read("general.file-association");
                bool jumpList = !bool.TryParse(values.GetValueOrDefault("general.jump-list"), out bool configuredJump) || configuredJump;
                try
                {
                    if (_autostart != autostart) { await DesktopSystemPreferences.ApplyAutostartAsync(autostart, _stop.Token).ConfigureAwait(false); _autostart = autostart; }
                    if (!OperatingSystem.IsMacOS() && _association != association)
                    { await DesktopSystemPreferences.ApplyFileAssociationsAsync(association, _stop.Token).ConfigureAwait(false); _association = association; }
                    if (OperatingSystem.IsWindows() && _jumpList != jumpList)
                    {
                        IReadOnlyList<string> captions = await _platform.LocalizeDesktopCaptionsAsync(["启动", "安装", "资源", "设置"], _stop.Token).ConfigureAwait(false);
                        string? error = await DesktopJumpList.ApplyAsync(jumpList, captions, _stop.Token).ConfigureAwait(false);
                        if (error is null) _jumpList = jumpList;
                        else if (!_stop.IsCancellationRequested) _log(error);
                    }
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException or PlatformNotSupportedException)
                { if (!_stop.IsCancellationRequested) _log("系统偏好应用失败：" + failure.Message); }
                finally { _initialReady.TrySetResult(); }
            }
        }
        catch (OperationCanceledException) { _initialReady.TrySetCanceled(_stop.Token); }
        catch (Exception error) { _initialReady.TrySetException(error); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _state.Changed -= OnState; _platform.WindowActivated -= OnActivated; _feedback.Changed -= OnFeedback;
        _work.Writer.TryComplete(); _policy.Writer.TryComplete(); await _stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_worker, _policyWorker).ConfigureAwait(false); _stop.Dispose();
    }
}
