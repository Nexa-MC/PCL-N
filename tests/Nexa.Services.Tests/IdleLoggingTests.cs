using Nexa.Services.Logging;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void LoggingWakesOnlyForPendingBatches()
    {
        using var clock = new LogPublicationClock();
        var builder = new XsrStateStoreBuilder();
        LogService.DeclareState(builder);
        var store = builder.Build();
        var id = store.Resolve(LogService.EntriesKey);
        using var log = new LogService(store, 16, clock, TimeSpan.FromMilliseconds(250));
        clock.Advance(TimeSpan.FromHours(8));
        AssertEqual(0, clock.Callbacks);
        AssertFalse(clock.Scheduled);
        log.Info("Idle", "one");
        log.Info("Idle", "two");
        AssertTrue(clock.Scheduled);
        clock.Advance(TimeSpan.FromMilliseconds(249));
        AssertEqual(0, store.ReadCollection<LogEntry>(id).Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        AssertEqual(2, store.ReadCollection<LogEntry>(id).Count);
        AssertEqual(1, clock.Callbacks);
        AssertFalse(clock.Scheduled);
        clock.Advance(TimeSpan.FromHours(8));
        AssertEqual(1, clock.Callbacks);

        bool reenter = true;
        store.Changed += _ =>
        {
            if (!reenter) return;
            reenter = false;
            log.Info("Idle", "reentrant observer");
        };
        log.Info("Idle", "trigger");
        clock.Advance(TimeSpan.FromMilliseconds(250));
        AssertEqual(3, store.ReadCollection<LogEntry>(id).Count);
        AssertTrue(clock.Scheduled);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        AssertEqual(4, store.ReadCollection<LogEntry>(id).Count);
        AssertFalse(clock.Scheduled);

        log.Info("Idle", "manual flush");
        log.FlushPending();
        AssertEqual(5, store.ReadCollection<LogEntry>(id).Count);
        AssertFalse(clock.Scheduled);
        log.Info("Idle", "clear pending");
        log.Clear();
        AssertEqual(0, store.ReadCollection<LogEntry>(id).Count);
        AssertFalse(clock.Scheduled);
        log.Info("Idle", "final drain");
        log.Dispose();
        AssertEqual(1, store.ReadCollection<LogEntry>(id).Count);
        AssertFalse(clock.Scheduled);
        clock.Advance(TimeSpan.FromHours(8));
        AssertEqual(3, clock.Callbacks);
        log.Info("Idle", "after dispose");
        AssertEqual(1, store.ReadCollection<LogEntry>(id).Count);
    }

    private static async ValueTask FileLoggingIsLazyAndDrainsOrderedBursts()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "launcher.log");
            await new FileLogSink(path).DisposeAsync();
            AssertFalse(File.Exists(path));
            var sink = new FileLogSink(path);
            var entry = new LogEntry(1, DateTimeOffset.UtcNow, LogLevel.Info, "Burst", "line", null);
            for (int index = 0; index < 4096; index++)
                sink.Write(entry, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await sink.DisposeAsync();
            string[] lines = await File.ReadAllLinesAsync(path);
            AssertEqual(4096, lines.Length);
            for (int index = 0; index < lines.Length; index++)
                AssertEqual(index.ToString(System.Globalization.CultureInfo.InvariantCulture), lines[index]);
            sink.Write(entry, "after disposal");
            await sink.DisposeAsync();
            AssertEqual(4096, (await File.ReadAllLinesAsync(path)).Length);

            // Opening a directory as a file fails; a failed mirror still drains shutdown.
            var failed = new FileLogSink(root);
            for (int index = 0; index < 512; index++) failed.Write(entry, "IO failure");
            await failed.DisposeAsync();
            AssertTrue(Directory.Exists(root));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class LogPublicationClock : TimeProvider, IDisposable
    {
        private PublicationTimer? _timer;
        public int Callbacks;
        public bool Scheduled => _timer?.Scheduled == true;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            AssertEqual(Timeout.InfiniteTimeSpan, period);
            return _timer = new PublicationTimer(this, callback, state, dueTime);
        }
        public void Advance(TimeSpan elapsed) => _timer?.Advance(elapsed);
        public void Dispose() => _timer?.Dispose();
        private sealed class PublicationTimer(LogPublicationClock clock, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private bool _disposed;
            private TimeSpan _remaining = dueTime;
            public bool Scheduled => !_disposed && _remaining != Timeout.InfiniteTimeSpan;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                AssertEqual(Timeout.InfiniteTimeSpan, period);
                if (_disposed) return false;
                _remaining = dueTime;
                return true;
            }
            public void Advance(TimeSpan elapsed)
            {
                if (!Scheduled) return;
                _remaining -= elapsed;
                if (_remaining > TimeSpan.Zero) return;
                _remaining = Timeout.InfiniteTimeSpan;
                clock.Callbacks++;
                callback(state);
            }
            public void Dispose() { _disposed = true; _remaining = Timeout.InfiniteTimeSpan; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
