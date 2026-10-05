namespace Nexa.Services.Downloads;

/// <summary>Host-owned transfer-body budget. Existing operations keep their admitted generation.</summary>
public sealed class DownloadBandwidthLimiter(Func<long> limitBytesPerSecond, TimeProvider? timeProvider = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private DownloadBandwidthBudget? _current;

    public DownloadBandwidthBudget Capture()
    {
        long limit = limitBytesPerSecond();
        if (limit is < 0 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(limitBytesPerSecond));
        lock (_gate)
        {
            if (_current?.BytesPerSecond != limit) _current = new(limit, _clock);
            return _current;
        }
    }
}

/// <summary>Shared by all operations admitted under one immutable bandwidth policy.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "This semaphore is memory-only; generations remain valid for admitted operations and never create a WaitHandle.")]
public sealed class DownloadBandwidthBudget
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private double _permitAt;
    private bool _started;
    public long BytesPerSecond { get; }
    public int MaximumReadBytes => BytesPerSecond == 0 ? int.MaxValue : (int)Math.Clamp(BytesPerSecond / 10, 1, 128 * 1024);

    internal DownloadBandwidthBudget(long bytesPerSecond, TimeProvider clock) { BytesPerSecond = bytesPerSecond; _clock = clock; }

    /// <summary>Accounts received bytes, carries fractional time, and releases admission on cancellation.</summary>
    public async ValueTask WaitAsync(int bytes, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        token.ThrowIfCancellationRequested();
        if (bytes == 0 || BytesPerSecond == 0) return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long now = _clock.GetTimestamp();
            double frequency = _clock.TimestampFrequency;
            if (!_started) { _permitAt = now; _started = true; }
            // Carry timer rounding forward, but never bank more than one millisecond
            // of idle time. The shared gate serializes this accounting across transfers.
            _permitAt = Math.Max(_permitAt, now - frequency / 1000) + bytes * frequency / BytesPerSecond;
            double milliseconds = (_permitAt - now) * 1000 / frequency;
            if (milliseconds > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(milliseconds)), _clock, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
