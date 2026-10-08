using System.Globalization;
using Nexa.Services.Logging;
using Nexa.Xsr;
using Nexa.Xsr.Diagnostics;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Services.Composition;

/// <summary>One bounded metadata trace, with observer composition preserving existing log ports.</summary>
public sealed class RuntimeTraceSession : IXsrDispatchObserver, IXsrStateObserver, IXsrEventObserver,
    IXsrSchedulerObserver, IXsrLifecycleObserver
{
    private readonly XsrSessionTrace _trace = new(XsrSessionId.Create(), 256);
    public void Register(XsrQueryRouterBuilder queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        queries.Register<RuntimeTraceQuery, RuntimeTraceSnapshot>(RuntimeTraceContract.Read,
            (_, token) => { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(XsrResult.Success(Capture())); });
    }
    public RuntimeTraceSnapshot Capture() => new(_trace.Capacity, _trace.DroppedCount,
        Array.AsReadOnly(_trace.Snapshot().Select(entry => new RuntimeTraceEntry(entry.Kind.ToString(),
            Safe(entry.SemanticId.Value), entry.CorrelationId.IsAssigned ? entry.CorrelationId.ToString() : "",
            entry.Timestamp, entry.Detail, entry.IsSuccess)).ToArray()));
    public void OnStarted(XsrDispatchStarted observation) => Record(observation.Kind == XsrDispatchKind.Command ? XsrTraceKind.Command : XsrTraceKind.Query,
        observation.SemanticId, observation.CorrelationId, "started", true);
    public void OnCompleted(XsrDispatchObservation observation) => Record(observation.Kind == XsrDispatchKind.Command ? XsrTraceKind.Command : XsrTraceKind.Query,
        observation.SemanticId, observation.CorrelationId,
        FormattableString.Invariant($"completed duration_ms={observation.Duration.TotalMilliseconds:F1}"), observation.IsSuccess);
    public void OnChanged(XsrStateChange change)
    {
        string id = change.SemanticId.Value;
        if (id.StartsWith("logging.", StringComparison.Ordinal) || id.StartsWith("diagnostics.", StringComparison.Ordinal)
            || id.StartsWith("telemetry.", StringComparison.Ordinal)) return;
        Record(XsrTraceKind.State, change.SemanticId, default,
            FormattableString.Invariant($"{change.Reason} revision={change.Revision} availability={change.Availability}"), true);
    }
    public void OnPublished(XsrEventPublication publication) => Record(XsrTraceKind.Event, publication.SemanticId,
        publication.CorrelationId, "sequence=" + publication.Sequence.ToString(CultureInfo.InvariantCulture), true);
    public void OnExecuted(XsrScheduledObservation observation) => Record(XsrTraceKind.Scheduled, default,
        observation.CorrelationId, FormattableString.Invariant($"{observation.Outcome} duration_ms={observation.Duration.TotalMilliseconds:F1}"),
        observation.Outcome != XsrScheduledOutcome.Faulted);
    public void OnPhaseChanged(XsrLifecycleTransition transition) => Record(XsrTraceKind.Lifecycle, default, default,
        transition.From + " -> " + transition.To, transition.To != XsrLifecyclePhase.Failed);
    private void Record(XsrTraceKind kind, XsrSemanticId semanticId, XsrCorrelationId correlation, string detail, bool success)
        => _trace.Record(new(kind, semanticId, correlation, System.Diagnostics.Stopwatch.GetTimestamp(), detail, success));
    private static string Safe(string? value) => value is null || value.Length == 0 ? ""
        : value.Length <= 256 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':') ? value : "<redacted>";

    public static IXsrDispatchObserver Combine(IXsrDispatchObserver first, IXsrDispatchObserver second) => new DispatchPair(first, second);
    public static IXsrStateObserver Combine(IXsrStateObserver first, IXsrStateObserver second) => new StatePair(first, second);
    public static IXsrEventObserver Combine(IXsrEventObserver first, IXsrEventObserver second) => new EventPair(first, second);
    public static IXsrSchedulerObserver Combine(IXsrSchedulerObserver first, IXsrSchedulerObserver second) => new SchedulerPair(first, second);
    public static IXsrLifecycleObserver Combine(IXsrLifecycleObserver first, IXsrLifecycleObserver second) => new LifecyclePair(first, second);
    private static void Observe(Action action)
    { try { action(); } catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { } }
    private sealed class DispatchPair(IXsrDispatchObserver first, IXsrDispatchObserver second) : IXsrDispatchObserver
    {
        public void OnStarted(XsrDispatchStarted value) { Observe(() => first.OnStarted(value)); Observe(() => second.OnStarted(value)); }
        public void OnCompleted(XsrDispatchObservation value) { Observe(() => first.OnCompleted(value)); Observe(() => second.OnCompleted(value)); }
    }
    private sealed class StatePair(IXsrStateObserver first, IXsrStateObserver second) : IXsrStateObserver
    { public void OnChanged(XsrStateChange value) { Observe(() => first.OnChanged(value)); Observe(() => second.OnChanged(value)); } }
    private sealed class EventPair(IXsrEventObserver first, IXsrEventObserver second) : IXsrEventObserver
    { public void OnPublished(XsrEventPublication value) { Observe(() => first.OnPublished(value)); Observe(() => second.OnPublished(value)); } }
    private sealed class SchedulerPair(IXsrSchedulerObserver first, IXsrSchedulerObserver second) : IXsrSchedulerObserver
    { public void OnExecuted(XsrScheduledObservation value) { Observe(() => first.OnExecuted(value)); Observe(() => second.OnExecuted(value)); } }
    private sealed class LifecyclePair(IXsrLifecycleObserver first, IXsrLifecycleObserver second) : IXsrLifecycleObserver
    { public void OnPhaseChanged(XsrLifecycleTransition value) { Observe(() => first.OnPhaseChanged(value)); Observe(() => second.OnPhaseChanged(value)); } }
}
