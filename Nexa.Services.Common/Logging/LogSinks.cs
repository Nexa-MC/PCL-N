using System.Diagnostics;
using System.Text;

namespace Nexa.Services.Logging;

/// <summary>
/// Mirrors log entries to the process console. Active only when the process actually has a
/// console output stream (launched from a terminal or with redirected output); detached GUI
/// launches disable the sink instead of paying for writes nobody sees.
/// </summary>
public sealed class ConsoleLogSink : ILogSink
{
    private bool _disabled;

    public void Write(LogEntry entry, string formattedLine)
    {
        if (_disabled)
        {
            return;
        }

        try
        {
            Console.WriteLine(formattedLine);
        }
        catch (Exception)
        {
            // No console (detached GUI launch) or a broken stdout: stop mirroring instead of
            // touching the log path on every entry.
            _disabled = true;
        }
    }
}

/// <summary>
/// Appends log entries to one UTF-8 file, opening lazily and disabling itself when the file
/// cannot be written (locked disk, missing folder); logging must never break the app.
/// </summary>
public sealed class FileLogSink : ILogSink, IDisposable, IAsyncDisposable
{
    private readonly string _filePath;
    private readonly System.Threading.Channels.Channel<string> _lines =
        System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(8192)
        { SingleReader = true, FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });
    private readonly Task _drain;
    private long _dropped;
    private int _disposed;

    public FileLogSink(string filePath)
    {
        _filePath = filePath;
        _drain = Task.Run(DrainAsync);
    }

    public void Write(LogEntry entry, string formattedLine)
    {
        if (Volatile.Read(ref _disposed) == 0 && !_lines.Writer.TryWrite(formattedLine))
            Interlocked.Increment(ref _dropped);
    }

    private async Task DrainAsync()
    {
        StreamWriter? writer = null;
        try
        {
            long flushedAt = Stopwatch.GetTimestamp();
            while (await _lines.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                int drained = 0;
                while (drained++ < 256 && _lines.Reader.TryRead(out var line))
                {
                    if (writer is null)
                    {
                        string? directory = Path.GetDirectoryName(_filePath);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                        writer = new StreamWriter(new FileStream(_filePath, FileMode.Append, FileAccess.Write,
                            FileShare.Read, 65536, useAsync: true), new UTF8Encoding(false), 65536);
                    }
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
                if (writer is not null && (!_lines.Reader.TryPeek(out _)
                    || Stopwatch.GetElapsedTime(flushedAt) >= TimeSpan.FromMilliseconds(200)))
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                    flushedAt = Stopwatch.GetTimestamp();
                }
            }
            long dropped = Interlocked.Read(ref _dropped);
            if (writer is not null && dropped > 0)
                await writer.WriteLineAsync($"[Logging] File sink omitted {dropped} lines because its bounded queue was full.").ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _lines.Writer.TryComplete(); }
        finally
        {
            // A disabled sink must not retain a failed IO burst in its bounded channel.
            while (_lines.Reader.TryRead(out _)) { }
            if (writer is not null)
                try { await writer.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Closes admission without blocking; DisposeAsync also drains and flushes.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _lines.Writer.TryComplete();
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _drain.ConfigureAwait(false);
    }
}
