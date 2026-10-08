using System.Diagnostics;
using Nexa.Core.Media;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiLocalMediaPlayer
{
    private string _warmedPath = "";
    private PngImage? _firstFrame;

    /// <summary>Checks the configured local engine and decodes one bounded video frame while hidden.</summary>
    public async Task<PngImage?> WarmUpAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_path.Length == 0) return null;
            if (_warmedPath == _path) return _firstFrame;
            if (!File.Exists(_path)) { Publish(AvaloniaUiMediaStatus.Failed, "Local media file is missing."); return null; }
            if (new FileInfo(_path).Length > 512L * 1048576)
            { Publish(AvaloniaUiMediaStatus.Failed, "Local media exceeds 512 MiB."); return null; }
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            bounded.CancelAfter(TimeSpan.FromSeconds(4));
            var start = new ProcessStartInfo(_video ? "ffmpeg" : "ffplay")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            string[] arguments = _video
                ? ["-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1", "-i", _path,
                    "-an", "-vf", "scale=960:540:force_original_aspect_ratio=decrease", "-frames:v", "1",
                    "-f", "image2pipe", "-vcodec", "png", "-threads", "1", "pipe:1"]
                : ["-version"];
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ?? throw new IOException("Media engine did not start.");
            using var kill = bounded.Token.Register(() => Kill(process));
            Task errors = DrainErrorsAsync(process.StandardError.BaseStream, bounded.Token);
            try
            {
                _firstFrame = _video
                    ? await ReadFrameAsync(process.StandardOutput.BaseStream, bounded.Token).ConfigureAwait(false)
                    : null;
                if (!_video) await DrainErrorsAsync(process.StandardOutput.BaseStream, bounded.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
                if (process.ExitCode != 0 || _video && _firstFrame is null)
                { Publish(AvaloniaUiMediaStatus.Failed, "Local media warmup failed."); return null; }
                _warmedPath = _path;
                return _firstFrame;
            }
            finally
            {
                Kill(process);
                try { await errors.ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        { Publish(AvaloniaUiMediaStatus.DependencyMissing, (_video ? "ffmpeg" : "ffplay") + " is not installed."); return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { Publish(AvaloniaUiMediaStatus.Failed, "Local media warmup timed out."); return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Publish(AvaloniaUiMediaStatus.Failed, error.Message); return null; }
        finally { _gate.Release(); }
    }
}
