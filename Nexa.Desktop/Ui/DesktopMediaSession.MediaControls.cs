using System.Globalization;
using Nexa.Services.Settings;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class DesktopMediaSession
{
    private AvaloniaUiMprisSession? _mpris;
    private IAsyncDisposable? _nativeMedia;
    private void InitializeMediaControls()
    {
        if (!OperatingSystem.IsLinux())
        {
            _platform.PostToWindow(() =>
            {
                if (_disposed) return;
                _nativeMedia = _platform.CreateNativeMediaControls(() => _audio.State,
                    () => { lock (_gate) return !_disposed && Read("music.enabled", false) && Read("music.media-controls", true); },
                    command => OnSystemMediaControl(command, null), _log);
            });
            return;
        }
        _mpris = new(() => _audio.State, CaptureMediaVolume, OnSystemMediaControl, _log,
            () => { lock (_gate) return !_disposed && Read("music.enabled", false) && Read("music.media-controls", true); });
    }
    private double CaptureMediaVolume()
    {
        lock (_gate) return _muted ? 0 : int.TryParse(_values.GetValueOrDefault("music.volume"), out int value) ? Math.Clamp(value, 0, 100) / 100.0 : .5;
    }
    private void OnSystemMediaControl(string command, double? value)
    {
        _platform.PostToWindow(() =>
        {
            lock (_gate) { if (_disposed || !Read("music.media-controls", true)) return; }
            if (command == "raise") { _platform.RestoreWindow(); return; }
            lock (_gate) { if (!Read("music.enabled", false)) return; }
            if (command == "volume" && value is { } volume)
            {
                _ = SaveMediaVolumeAsync(volume);
                return;
            }
            if (command is "play" or "pause")
            {
                lock (_gate) { _requested = command == "play"; }
                _updates.Writer.TryWrite(true);
                return;
            }
            _intents.Emit(XsrSemanticId.Parse("ui.media." + command), _shell.Root, XsrCorrelationId.Create());
        });
    }
    private async Task SaveMediaVolumeAsync(double volume)
    {
        if (!_commands.TryResolve(SettingsPolicyContract.SetCommand, out var route)) return;
        var result = await _commands.Dispatch(route, new SettingsMutation("music.volume", SettingsLayer.Global,
            new(SettingsOverrideMode.Custom, ((int)Math.Round(volume * 100)).ToString(CultureInfo.InvariantCulture))),
            cancellationToken: _stop.Token).Completion.ConfigureAwait(false);
        if (!result.IsSuccess && !_disposed) _log("系统音量未保存：" + result.Error?.Message);
    }
    private async ValueTask DisposeMediaControlsAsync()
    {
        if (_mpris is not null) { await _mpris.DisposeAsync().ConfigureAwait(false); _mpris = null; }
        if (_nativeMedia is not null) { await _nativeMedia.DisposeAsync().ConfigureAwait(false); _nativeMedia = null; }
    }
}
