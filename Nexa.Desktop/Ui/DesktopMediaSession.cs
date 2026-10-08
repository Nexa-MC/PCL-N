using System.Threading.Channels;
using Nexa.Core.Media;
using Nexa.Services.Scheduling;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>Local media preferences control real owned engines; optional work pauses at quiet boundaries.</summary>
internal sealed partial class DesktopMediaSession : IAsyncDisposable
{
    private static readonly XsrUiContextMenuItem[] OwnedMenuItems =
    [
        new("音乐播放 / 暂停", XsrSemanticId.Parse("ui.media.play-pause")),
        new("下一首音乐", XsrSemanticId.Parse("ui.media.next")),
        new("音乐静音 / 恢复", XsrSemanticId.Parse("ui.media.mute")),
        new("停止音乐", XsrSemanticId.Parse("ui.media.stop")),
    ];
    private readonly XsrQueryRouter _queries;
    private readonly XsrCommandRouter _commands;
    private readonly XsrStateStore _state;
    private readonly XsrStateId _revision, _quiet;
    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly AvaloniaUiPlatformActions _platform;
    private readonly Action<string> _log;
    private readonly AvaloniaUiLocalMediaPlayer _audio, _video;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<bool> _updates = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _worker;
    private readonly TaskCompletionSource _initialReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task InitialReady => _initialReady.Task;
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, string?> _values = new Dictionary<string, string?>();
    private bool _requested, _muted, _initialized, _disposed;
    private int _trackIndex;
    private int _selectedIndex = -1;
    private string _selectedSource = "", _selectedTrack = "";
    private PngImage? _pendingFrame;
    private int _framePosted;
    private readonly DesktopBackgroundPresentation _backgroundPresentation;
    private DesktopBackgroundAppearance _backgroundAppearance = DesktopBackgroundAppearance.Default;

    internal DesktopMediaSession(XsrQueryRouter queries, XsrCommandRouter commands, XsrStateStore state, XsrUiShell shell,
        DesktopUiIntentSink intents, AvaloniaUiPlatformActions platform, Action<string> log)
    {
        _queries = queries; _commands = commands; _state = state; _shell = shell; _intents = intents; _platform = platform; _log = log;
        _revision = state.Resolve(SettingsPolicyContract.RevisionKey); state.TryResolve(WorkSchedulingContract.QuietKey, out _quiet);
        _backgroundPresentation = DesktopBackgroundPresentation.GetOrCreate(shell.Tree, shell.Content);
        _audio = new(false); _video = new(true, PresentFrame);
        _audio.Changed += OnAudioState; _video.Changed += OnVideoState;
        _state.Changed += OnState; intents.IntentEmitted += OnIntent;
        platform.WindowActivityChanged += OnActivity; platform.MediaControlRequested += OnMediaControl;
        var oldMenu = shell.Tree.GetComponent<XsrUiContextMenu>(shell.Root)?.Items ?? [];
        shell.Tree.SetComponent(shell.Root, new XsrUiContextMenu(oldMenu.Where(item => !IsOwnedMenuCommand(item.Command)).Concat(OwnedMenuItems)));
        _worker = RunAsync(); ApplyPolicy(); InitializeMediaControls();
    }
    private void OnState(XsrStateChange change)
    {
        if (change.Id == _revision) ApplyPolicy();
        else if (change.Id == _quiet) _updates.Writer.TryWrite(true);
    }
    private void ApplyPolicy()
    {
        if (!_disposed) _updates.Writer.TryWrite(true);
    }
    private bool Read(string key, bool fallback) => bool.TryParse(_values.GetValueOrDefault(key), out bool parsed) ? parsed : fallback;
    private void OnActivity(bool active) => _updates.Writer.TryWrite(true);
    private void OnIntent(object? sender, DesktopUiIntentEventArgs args)
    {
        if (args.Intent.Source == _shell.Root && args.Intent.Command.Value.StartsWith("ui.media.", StringComparison.Ordinal))
            OnMediaControl(args.Intent.Command.Value[9..]);
    }
    private void OnMediaControl(string control)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _initialized = true;
            switch (control)
            {
                case "play-pause": _requested = !_requested; break;
                case "stop": _requested = false; break;
                case "mute": _muted = !_muted; break;
                case "next": _trackIndex++; _requested = true; break;
                default: return;
            }
        }
        _updates.Writer.TryWrite(true);
    }
    private void OnAudioState(AvaloniaUiMediaState state)
    {
        if (state.Status is AvaloniaUiMediaStatus.DependencyMissing or AvaloniaUiMediaStatus.Failed) _log("音乐：" + state.Detail);
        if (state.Status != AvaloniaUiMediaStatus.Completed) return;
        lock (_gate) { if (_disposed || !Read("music.autoplay", true)) return; _trackIndex++; }
        _updates.Writer.TryWrite(true);
    }
    private void OnVideoState(AvaloniaUiMediaState state)
    {
        if (state.Status is AvaloniaUiMediaStatus.DependencyMissing or AvaloniaUiMediaStatus.Failed) _log("视频背景：" + state.Detail);
        if (state.Status != AvaloniaUiMediaStatus.Playing) _platform.PostToWindow(RestoreBackground);
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (bool ignored in _updates.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    var settings = await CommittedSettingsRead.QueryAsync(_queries, _stop.Token).ConfigureAwait(false);
                    if (settings is null) { _initialReady.TrySetException(new IOException("无法读取初始媒体设置。")); continue; }
                    string audio, video; bool playAudio, playVideo, shuffle; int volume, index;
                    DesktopBackgroundAppearance appearance;
                    string? appearanceError = null;
                    lock (_gate)
                    {
                        _values = settings.Values.ToDictionary(item => item.Key, item => item.Value.Value);
                        try { appearance = DesktopBackgroundAppearance.Read(_values); }
                        catch (InvalidDataException error) { appearance = DesktopBackgroundAppearance.Default; appearanceError = error.Message; }
                        _backgroundAppearance = appearance;
                        if (!_initialized) { _requested = Read("music.startup", false); _initialized = true; }
                        bool quiet = _quiet.IsAssigned && _state.Read<WorkQuietSnapshot>(_quiet).Value.IsQuiet;
                        bool inactive = !_platform.IsWindowActive;
                        audio = _values.GetValueOrDefault("music.path") ?? "";
                        video = _values.GetValueOrDefault("appearance.video-path") ?? "";
                        playAudio = Read("music.enabled", false) && _requested && !(Read("music.auto-pause", true) && (quiet || inactive));
                        playVideo = VideoPlaybackAllowed(video, _values, quiet, inactive);
                        volume = _muted ? 0 : int.TryParse(_values.GetValueOrDefault("music.volume"), out int parsed) ? Math.Clamp(parsed, 0, 100) : 50;
                        shuffle = Read("music.shuffle", false); index = _trackIndex;
                    }
                    if (appearanceError is not null) _log("背景配置未应用：" + appearanceError);
                    _platform.PostToWindow(() => { if (!_disposed) _backgroundPresentation.SetAppearance(appearance); });
                    if (Directory.Exists(audio))
                    {
                        string[] tracks = Directory.EnumerateFiles(audio).Where(path => Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".ogg" or ".wav" or ".flac").Take(128).Order(StringComparer.Ordinal).ToArray();
                        if (_selectedIndex != index || _selectedSource != audio || !tracks.Contains(_selectedTrack, StringComparer.Ordinal))
                        {
                            _selectedSource = audio; _selectedIndex = index;
                            _selectedTrack = tracks.Length == 0 ? "" : tracks[shuffle ? Random.Shared.Next(tracks.Length) : (int)((uint)index % (uint)tracks.Length)];
                        }
                        audio = _selectedTrack;
                    }
                    await _audio.ConfigureAsync(audio, volume, playAudio, _stop.Token).ConfigureAwait(false);
                    await _video.ConfigureAsync(video, 0, playVideo, _stop.Token).ConfigureAwait(false);
                    if (!_initialReady.Task.IsCompleted)
                    {
                        if (Read("music.enabled", false)) await _audio.WarmUpAsync(_stop.Token).ConfigureAwait(false);
                        if (VideoPlaybackAllowed(video, _values, quiet: false, inactive: false)
                            && await _video.WarmUpAsync(_stop.Token).ConfigureAwait(false) is { } firstFrame)
                            _platform.PostToWindow(() => { if (!_disposed) _backgroundPresentation.SetVideo(firstFrame, appearance); });
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                { if (!_disposed) _log("媒体配置未应用：" + error.Message); }
                finally { _initialReady.TrySetResult(); }
            }
        }
        catch (OperationCanceledException) { _initialReady.TrySetCanceled(_stop.Token); }
        catch (Exception error) { _initialReady.TrySetException(error); throw; }
    }
    internal static bool VideoPlaybackAllowed(string path, IReadOnlyDictionary<string, string?> values, bool quiet, bool inactive)
    {
        bool ReadValue(string key, bool fallback) => bool.TryParse(values.GetValueOrDefault(key), out bool parsed) ? parsed : fallback;
        return path.Length > 0 && !ReadValue("appearance.reduced-motion", false) && !ReadValue("appearance.animations-disabled", false)
            && !(ReadValue("appearance.video-auto-pause", true) && (quiet || inactive));
    }
    private void PresentFrame(PngImage image)
    {
        Volatile.Write(ref _pendingFrame, image);
        if (Interlocked.CompareExchange(ref _framePosted, 1, 0) != 0) return;
        _platform.PostToWindow(() =>
        {
            Volatile.Write(ref _framePosted, 0);
            if (_disposed || _video.State.Status != AvaloniaUiMediaStatus.Playing || Volatile.Read(ref _pendingFrame) is not { } frame) return;
            DesktopBackgroundAppearance appearance;
            lock (_gate) appearance = _backgroundAppearance;
            _backgroundPresentation.SetVideo(frame, appearance);
        });
    }
    private void RestoreBackground()
    {
        if (!_disposed && _video.State.Status == AvaloniaUiMediaStatus.Playing) return;
        _backgroundPresentation.ClearVideo();
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _state.Changed -= OnState; _intents.IntentEmitted -= OnIntent;
        _platform.WindowActivityChanged -= OnActivity; _platform.MediaControlRequested -= OnMediaControl;
        if (_shell.Tree.GetComponent<XsrUiContextMenu>(_shell.Root) is { } menu)
        {
            var remaining = menu.Items.Where(item => !IsOwnedMenuCommand(item.Command)).ToArray();
            _shell.Tree.SetComponent(_shell.Root, remaining.Length == 0 ? null : new XsrUiContextMenu(remaining));
        }
        _updates.Writer.TryComplete(); await _stop.CancelAsync().ConfigureAwait(false); await _worker.ConfigureAwait(false);
        _audio.Changed -= OnAudioState; _video.Changed -= OnVideoState;
        await _audio.DisposeAsync().ConfigureAwait(false); await _video.DisposeAsync().ConfigureAwait(false);
        await DisposeMediaControlsAsync().ConfigureAwait(false);
        _platform.PostToWindow(RestoreBackground);
        _stop.Dispose();
    }
    private static bool IsOwnedMenuCommand(XsrSemanticId command) => OwnedMenuItems.Any(item => item.Command == command);
}
