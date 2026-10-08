using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Nexa.Core.Media;

namespace Nexa.UI.Next.Backend.Avalonia;

public enum AvaloniaUiMediaStatus { Stopped, Playing, Paused, Completed, DependencyMissing, Failed }
public sealed record AvaloniaUiMediaState(AvaloniaUiMediaStatus Status, string? Detail = null);

/// <summary>Owns one bounded local decoder/player process; never launches URI or shell input.</summary>
public sealed class AvaloniaUiLocalMediaPlayer : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly bool _video;
    private readonly Action<PngImage>? _frame;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _playing;
    private Process? _process;
    private Task _run = Task.CompletedTask;
    private string _path = "";
    private int _volume = 50;
    private double _position;
    private long _started;
    private AvaloniaUiMediaState _state = new(AvaloniaUiMediaStatus.Stopped);
    private bool _disposed;

    public AvaloniaUiLocalMediaPlayer(bool video, Action<PngImage>? frame = null)
    { _video = video; _frame = frame; }
    public AvaloniaUiMediaState State => Volatile.Read(ref _state);
    public event Action<AvaloniaUiMediaState>? Changed;

    public async Task ConfigureAsync(string localPath, int volume, bool play, CancellationToken token = default)
    {
        if (volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volume));
        if (localPath.Length > 4096 || localPath.Length != 0 && !Path.IsPathFullyQualified(localPath))
            throw new ArgumentException("Media requires a local absolute path.", nameof(localPath));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool changed = _path != localPath || _volume != volume;
            if (changed)
            {
                await StopProcessAsync().ConfigureAwait(false);
                if (_path != localPath) _position = 0;
                _path = localPath; _volume = volume;
            }
            if (!play || localPath.Length == 0)
            {
                await StopProcessAsync().ConfigureAwait(false);
                Publish(localPath.Length == 0 ? AvaloniaUiMediaStatus.Stopped : AvaloniaUiMediaStatus.Paused);
                return;
            }
            if (_process is not null && !_process.HasExited) return;
            if (_process is not null) { await StopProcessAsync().ConfigureAwait(false); _position = 0; }
            string executable = _video ? "ffmpeg" : "ffplay";
            if (!File.Exists(localPath)) { Publish(AvaloniaUiMediaStatus.Failed, "Local media file is missing."); return; }
            if (new FileInfo(localPath).Length > 512L * 1048576) { Publish(AvaloniaUiMediaStatus.Failed, "Local media exceeds 512 MiB."); return; }
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = _video,
                RedirectStandardError = true
            };
            string[] arguments = _video
                ? ["-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1", "-stream_loop", "-1", "-re", "-ss", _position.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", localPath, "-an", "-vf", "scale=960:540:force_original_aspect_ratio=decrease,fps=6", "-f", "image2pipe", "-vcodec", "png", "-threads", "1", "pipe:1"]
                : ["-hide_banner", "-loglevel", "error", "-nodisp", "-vn", "-autoexit", "-volume", volume.ToString(CultureInfo.InvariantCulture),
                    "-ss", _position.ToString("0.###", CultureInfo.InvariantCulture), "-i", localPath];
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            try
            {
                _process = Process.Start(start) ?? throw new IOException("Media engine did not start.");
                _playing = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _started = Stopwatch.GetTimestamp();
                Publish(AvaloniaUiMediaStatus.Playing);
                _run = RunAsync(_process, _playing.Token);
            }
            catch (System.ComponentModel.Win32Exception) { Publish(AvaloniaUiMediaStatus.DependencyMissing, executable + " is not installed."); }
            catch (IOException error) { Publish(AvaloniaUiMediaStatus.Failed, error.Message); }
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync(Process process, CancellationToken token)
    {
        Task drain = DrainErrorsAsync(process.StandardError.BaseStream, token);
        try
        {
            if (_video)
                while (!token.IsCancellationRequested)
                {
                    PngImage? image = await ReadFrameAsync(process.StandardOutput.BaseStream, token).ConfigureAwait(false);
                    if (image is null) break;
                    _frame?.Invoke(image);
                }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            if (!token.IsCancellationRequested) Publish(process.ExitCode == 0 ? AvaloniaUiMediaStatus.Completed : AvaloniaUiMediaStatus.Failed,
                process.ExitCode == 0 ? null : "Media engine returned " + process.ExitCode);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException)
        {
            if (!token.IsCancellationRequested) { Kill(process); Publish(AvaloniaUiMediaStatus.Failed, error.GetType().Name); }
        }
        finally { try { await drain.ConfigureAwait(false); } catch (Exception error) when (error is OperationCanceledException or IOException) { } }
    }
    private static async Task DrainErrorsAsync(Stream stream, CancellationToken token)
    {
        // Drain continuously with fixed memory; arbitrary stderr never becomes a retained log/string.
        byte[] buffer = new byte[4096];
        while (await stream.ReadAsync(buffer, token).ConfigureAwait(false) != 0) { }
    }
    internal static async Task<PngImage?> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        byte[] signature = new byte[8];
        int first = await stream.ReadAsync(signature.AsMemory(0, 1), token).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(signature.AsMemory(1), token).ConfigureAwait(false);
        if (!signature.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("Invalid media frame signature.");
        using var encoded = new MemoryStream();
        encoded.Write(signature);
        byte[] header = new byte[8];
        while (true)
        {
            await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            if (length > 4 * 1048576 || encoded.Length + length + 12 > 4 * 1048576) throw new InvalidDataException("Media frame exceeds 4 MiB.");
            encoded.Write(header);
            byte[] payload = new byte[(int)length + 4];
            await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
            encoded.Write(payload);
            if (header.AsSpan(4).SequenceEqual("IEND"u8)) break;
        }
        return PngImage.TryCreatePreview(encoded.GetBuffer().AsSpan(0, (int)encoded.Length)) ?? throw new InvalidDataException("Invalid media frame dimensions.");
    }
    private async Task StopProcessAsync()
    {
        if (_process is not { } process) return;
        _position += Stopwatch.GetElapsedTime(_started).TotalSeconds;
        if (_playing is not null) await _playing.CancelAsync().ConfigureAwait(false);
        Kill(process);
        await _run.ConfigureAwait(false);
        process.Dispose(); _process = null; _playing?.Dispose(); _playing = null;
    }
    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    private void Publish(AvaloniaUiMediaStatus status, string? detail = null)
    { var state = new AvaloniaUiMediaState(status, detail); Volatile.Write(ref _state, state); Changed?.Invoke(state); }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; await _lifetime.CancelAsync().ConfigureAwait(false); await StopProcessAsync().ConfigureAwait(false); _lifetime.Dispose(); }
        finally { _gate.Release(); }
    }
}
