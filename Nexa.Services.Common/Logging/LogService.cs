using System.Runtime.CompilerServices;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Logging;

/// <summary>
/// The logging capability: a bounded, ordered ring of redacted log entries published as one
/// ordered state collection, so every surface that shows logs reads local state instead of
/// reaching into a mutable shared list. There is no static global sink — services receive this
/// service through the composition root. Publication never breaks the operation being logged.
/// </summary>
public sealed class LogService : ILogWriter, IDisposable
{
    void ILogWriter.Write(LogLevel level, string subsystem, string message, string? exceptionText) => Write(level, subsystem, message, exceptionText);

    internal void ObserveOperation(string module, TimeSpan duration, bool succeeded)
    {
        ILogSink[] sinks;
        lock (_gate) sinks = [.. _sinks];
        foreach (var sink in sinks)
        {
            if (sink is not ILogOperationSink observer) continue;
            try { observer.OnOperation(module, duration, succeeded); }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
        }
    }

    public const string OwnerName = "Nexa.Services.Logging";

    /// <summary>
    /// The ordered collection state key: items are <see cref="LogEntry"/>, keyed by sequence.
    /// </summary>
    public static readonly XsrSemanticId EntriesKey = XsrSemanticId.Parse("logging.entries");

    private const int MaxAppendConflicts = 8;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly TimeProvider _clock;
    private readonly XsrStateStore _store;
    private readonly XsrStateId _entriesId;
    private int _maximumLevel = (int)LogLevel.Info;
    private long _sequence;
    private readonly Queue<LogEntry> _ring = new();
    private readonly ITimer? _publicationTimer;
    private readonly TimeSpan _publicationInterval;
    private bool _pending, _publicationScheduled, _disposed;
    private readonly List<ILogSink> _sinks = [];

    /// <summary>
    /// Two-phase composition, declaration phase: registers the ordered entries collection
    /// into the shared host builder.
    /// </summary>
    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Collection<LogEntry, long>(
            EntriesKey,
            OwnerName,
            static entry => entry.Sequence);
    }

    public LogService(XsrStateStore store, int capacity = 2_000, TimeProvider? clock = null)
        : this(store, capacity, clock, null) { }

    public LogService(XsrStateStore store, int capacity, TimeProvider? clock, TimeSpan? publicationInterval)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _store = store ?? throw new ArgumentNullException(nameof(store));

        _capacity = capacity;
        _clock = clock ?? TimeProvider.System;
        _entriesId = _store.Resolve(EntriesKey);
        if (publicationInterval is { } interval && interval > TimeSpan.Zero)
        {
            _publicationInterval = interval;
            var tick = new PublicationTick(this);
            _publicationTimer = tick.Timer = _clock.CreateTimer(static state => ((PublicationTick)state!).Run(),
                tick, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Adds one mirror sink (console, file). Sinks observe recorded entries after redaction and
    /// state publication; sink failures are isolated from the operation being logged.
    /// </summary>
    public void AddSink(ILogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            _sinks.Add(sink);
        }
    }

    public int Capacity => _capacity;

    public XsrStateStore StateStore => _store;

    /// <summary>
    /// The most verbose level that is recorded. The default is <see cref="LogLevel.Info"/>.
    /// Invalid values fall back to Info, mirroring the legacy gate.
    /// </summary>
    public LogLevel MaximumLevel
    {
        get => (LogLevel)Volatile.Read(ref _maximumLevel);
        set => Volatile.Write(
            ref _maximumLevel,
            (int)(Enum.IsDefined(value) ? value : LogLevel.Info));
    }

    public bool IsEnabled(LogLevel level) =>
        Enum.IsDefined(level) && (int)level <= (int)MaximumLevel;

    /// <summary>
    /// Whether the verbose tiers (<see cref="Debug"/> and <see cref="Trace"/>) record at all.
    /// Hot loops check this once instead of paying string construction per iteration.
    /// </summary>
    public bool VerboseEnabled => IsEnabled(LogLevel.Debug);

    public LogOperation BeginOperation(string module, string name, string? context = null, LogLevel level = LogLevel.Info,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        new(this, module, name, context, $"{Path.GetFileName(file)}:{line}", level);

    // Ergonomic manual log points: one call per statement instead of spelling the level enum
    // at every call site. Info marks user-visible operations, Debug marks one-shot internals,
    // Trace marks hot loops (the RealTime tier), and Warn/Error mark failures.

    public void Info(string module, string message) => Write(LogLevel.Info, module, message);

    public void Warn(string module, string message) => Write(LogLevel.Warn, module, message);

    public void Error(string module, string message, string? exceptionText = null) =>
        Write(LogLevel.Error, module, message, exceptionText);

    public void Debug(string module, string message) => Write(LogLevel.Debug, module, message);

    /// <summary>Writes at the RealTime tier, reserved for important loop bodies (segments,
    /// retries, per-item scans) whose volume is only useful while chasing a live bug.</summary>
    public void Trace(string module, string message) => Write(LogLevel.RealTime, module, message);

    /// <summary>
    /// Normalizes, redacts, and records one entry when its level passes the gate. The module is
    /// trimmed or defaults to "General"; the message and exception text are redacted before
    /// storage. This method never throws into the operation being logged.
    /// </summary>
    public void Write(LogLevel level, string module, string message, string? exceptionText = null)
        => WriteCore(level, module, message, exceptionText, null);

    internal void WriteOperation(LogLevel level, string module, string message, string? exceptionText, DiagnosticOperationFacts facts)
        => WriteCore(level, module, message, exceptionText, facts);

    private void WriteCore(LogLevel level, string module, string message, string? exceptionText, DiagnosticOperationFacts? facts)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        string redacted = LogRedactor.Redact(message);
        string? error = string.IsNullOrWhiteSpace(exceptionText) ? null : LogRedactor.Redact(exceptionText);
        lock (_gate)
        {
            if (_disposed) return;
            LogEntry entry = new(++_sequence, _clock.GetUtcNow(), level,
                string.IsNullOrWhiteSpace(module) ? "General" : module.Trim(), redacted, error)
            { Operation = facts };
            _ring.Enqueue(entry);
            if (_ring.Count > _capacity) _ring.Dequeue();
            _pending = true;
            if (_publicationTimer is null) FlushPending();
            else SchedulePublication();
            MirrorToSinks(entry);
        }
    }

    private void MirrorToSinks(LogEntry entry)
    {
        if (_sinks.Count == 0)
        {
            return;
        }

        string line = entry.ToDisplayText();
        lock (_gate)
        {
            foreach (ILogSink sink in _sinks)
            {
                try
                {
                    sink.Write(entry, line);
                }
                catch (Exception)
                {
                    // Sinks are mirrors; a failing sink never breaks the log operation.
                }
            }
        }
    }

    /// <summary>
    /// One coherent read of the current ring, oldest first.
    /// </summary>
    public IReadOnlyList<LogEntry> GetSnapshot()
    {
        lock (_gate) { FlushPending(); return Array.AsReadOnly(_ring.ToArray()); }
    }

    /// <summary>
    /// Empties the ring and its state collection.
    /// </summary>
    public void Clear()
    {
        lock (_gate) { _ring.Clear(); _pending = true; FlushPending(); }
    }

    /// <summary>Publishes one bounded batch; diagnostic callers may request an immediate flush.</summary>
    public void FlushPending()
    {
        lock (_gate)
        {
            if (_publicationScheduled)
            {
                _publicationTimer!.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _publicationScheduled = false;
            }
            if (!_pending) return;
            long sequence = _sequence;
            LogEntry[] items = _ring.ToArray();
            long oldest = items.Length == 0 ? long.MaxValue : items[0].Sequence;
            for (int attempt = 0; attempt < MaxAppendConflicts; attempt++)
            {
                var snapshot = _store.ReadCollection<LogEntry>(_entriesId);
                long newestPublished = snapshot.Count == 0 ? 0 : snapshot.Items[^1].Sequence;
                var result = _store.PublishDelta(_entriesId, new XsrCollectionDelta<LogEntry, long>(
                    snapshot.Revision, items.Where(entry => entry.Sequence > newestPublished).ToArray(),
                    snapshot.Items.Where(entry => entry.Sequence < oldest).Select(entry => entry.Sequence).ToArray()));
                if (result.IsApplied)
                {
                    _pending = _sequence != sequence;
                    if (_pending) SchedulePublication();
                    return;
                }
            }
            SchedulePublication();
        }
    }

    // Caller holds _gate. A reentrant state observer may append during publication.
    private void SchedulePublication()
    {
        if (_disposed || _publicationTimer is null || _publicationScheduled) return;
        _publicationScheduled = true;
        _publicationTimer.Change(_publicationInterval, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            FlushPending();
            _publicationTimer?.Dispose();
        }
    }

    private sealed class PublicationTick(LogService owner)
    {
        private readonly WeakReference<LogService> _owner = new(owner);
        internal ITimer? Timer;
        internal void Run()
        {
            if (_owner.TryGetTarget(out var target)) target.FlushPending();
            else Timer?.Dispose();
        }
    }
}
