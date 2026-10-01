using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Nexa.Services.Logging;

/// <summary>
/// Explicit, low-volume breadcrumbs for one operation. The owner supplies safe identifiers,
/// never request objects, credentials or localized UI messages. No ambient/global context is used.
/// </summary>
public sealed class LogOperation : IDisposable
{
    private readonly LogService _log;
    private readonly object _gate = new();
    private readonly string _module;
    private readonly string _name;
    private readonly LogLevel _level;
    private readonly string? _context;
    private string? _stageContext;
    private string _source;
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private string _stage = "begin";
    private bool _finished;

    internal LogOperation(LogService log, string module, string name, string? context, string source, LogLevel level)
    {
        _log = log;
        _module = module;
        _name = name;
        _level = level;
        _context = context;
        _source = source;
        Id = Guid.NewGuid().ToString("N");
        Write(_level, "started");
    }

    public string Id { get; }

    public void Stage(string stage, string? context = null,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        lock (_gate)
        {
            if (_finished) return;
            _stage = stage;
            _stageContext = context;
            _source = $"{Path.GetFileName(file)}:{line}";
            Write(_level, "entered");
        }
    }

    public void Complete(string? context = null) => Finish(_level, $"completed {context}");

    public void Reject(string code) => Finish(LogLevel.Warn, $"rejected code={code}");

    public void Cancel() => Finish(_level, "cancelled");

    public void Fail(Exception exception) => Finish(LogLevel.Error, "failed", ExceptionDiagnostics.Describe(exception));

    public void Dispose() => Finish(LogLevel.Warn, "ended without a terminal outcome");

    private void Finish(LogLevel level, string outcome, string? exceptionText = null)
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
            Write(level, outcome, exceptionText);
            _log.ObserveOperation(_module, Stopwatch.GetElapsedTime(_startedAt), outcome.StartsWith("completed", StringComparison.Ordinal));
        }
    }

    private void Write(LogLevel level, string detail, string? exceptionText = null) =>
        _log.WriteOperation(level, _module, string.Create(CultureInfo.InvariantCulture,
            $"{_name} op={Id} stage={_stage} {detail} {_context} {_stageContext} source={_source} elapsed_ms={Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds:F1}"), exceptionText,
            new(_name, _stage, detail.StartsWith("completed", StringComparison.Ordinal) ? DiagnosticOperationOutcome.Completed
                : detail.StartsWith("rejected", StringComparison.Ordinal) ? DiagnosticOperationOutcome.Rejected
                : detail == "started" ? DiagnosticOperationOutcome.Started : detail == "entered" ? DiagnosticOperationOutcome.Entered
                : detail == "cancelled" ? DiagnosticOperationOutcome.Cancelled : detail == "failed" ? DiagnosticOperationOutcome.Failed
                : DiagnosticOperationOutcome.Unfinished));
}
