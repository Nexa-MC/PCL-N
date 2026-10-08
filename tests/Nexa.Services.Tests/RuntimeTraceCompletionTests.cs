using Nexa.Services.Composition;
using Nexa.Services.Logging;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask RuntimeTraceComposesRealObserversAndKeepsOnlyBoundedMetadata()
    {
        const string secret = "private-payload-path-account";
        var trace = new RuntimeTraceSession();
        var existing = new ExistingTraceObserver();
        var correlation = XsrCorrelationId.Create();
        var commands = new XsrCommandRouterBuilder();
        var commandId = XsrSemanticId.Parse("minecraft.fixture.start");
        commands.Register<TraceSecret>(commandId, (request, _) =>
        {
            AssertEqual(secret, request.Value);
            return ValueTask.FromResult(XsrResult.Failure(new XsrError(XsrErrorKind.Rejected,
                XsrSemanticId.Parse("fixture.rejected"), secret)));
        });
        var router = commands.Build(RuntimeTraceSession.Combine((IXsrDispatchObserver)existing, trace));
        AssertTrue(router.TryResolve(commandId, out var route));
        var dispatch = router.Dispatch(route, new TraceSecret(secret), correlation);
        AssertFalse((await dispatch.Completion).IsSuccess);
        AssertEqual(1, existing.Starts);
        AssertEqual(1, existing.Completions);

        var states = new XsrStateStoreBuilder();
        var stateKey = XsrSemanticId.Parse("fixture.state");
        states.Cell<TraceSecret>(stateKey, "TraceFixture");
        var store = states.Build(RuntimeTraceSession.Combine((IXsrStateObserver)existing, trace));
        store.Publish(store.Resolve(stateKey), new TraceSecret(secret));
        AssertEqual(1, existing.States);

        var events = new XsrEventRouterBuilder();
        var eventKey = XsrSemanticId.Parse("fixture.event");
        var scope = XsrSemanticId.Parse("fixture.scope");
        events.DeclareScope(scope, 8);
        events.Register<TraceSecret>(eventKey, scope, XsrEventOrdering.PerKey);
        var eventRouter = events.Build(RuntimeTraceSession.Combine((IXsrEventObserver)existing, trace));
        AssertTrue(eventRouter.Publish(eventRouter.Resolve(eventKey), new TraceSecret(secret), correlation, secret).IsSuccess);
        AssertEqual(1, existing.Events);

        var lifecycle = new XsrLifecycle(secret, RuntimeTraceSession.Combine((IXsrLifecycleObserver)existing, trace));
        lifecycle.Enter(XsrLifecyclePhase.Starting);
        lifecycle.Enter(XsrLifecyclePhase.Running);
        AssertEqual(2, existing.Transitions);
        using var scheduler = new XsrScheduler(observer: RuntimeTraceSession.Combine((IXsrSchedulerObserver)existing, trace));
        _ = scheduler.Schedule(TimeSpan.Zero, _ => ValueTask.CompletedTask, correlation);
        await existing.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AssertTrue(SpinWait.SpinUntil(() => trace.Capture().Entries.Any(entry => entry.Kind == "Scheduled"), TimeSpan.FromSeconds(5)));

        var snapshot = trace.Capture();
        AssertTrue(snapshot.Entries.Any(entry => entry.Kind == "Command" && entry.SemanticId == commandId.Value && !entry.IsSuccess));
        AssertTrue(snapshot.Entries.Any(entry => entry.Kind == "State" && entry.SemanticId == stateKey.Value));
        AssertTrue(snapshot.Entries.Any(entry => entry.Kind == "Event" && entry.CorrelationId == correlation.ToString()));
        AssertTrue(snapshot.Entries.Any(entry => entry.Kind == "Lifecycle"));
        AssertTrue(snapshot.Entries.All(entry => entry.MonotonicTimestamp > 0));
        AssertFalse(snapshot.Entries.Any(entry => entry.ToString().Contains(secret, StringComparison.Ordinal)));

        // These are real publications through the state store, not synthetic trace rows.
        for (int index = 0; index < 300; index++) store.Publish(store.Resolve(stateKey), new TraceSecret(secret));
        var bounded = trace.Capture();
        AssertEqual(256, bounded.Capacity);
        AssertEqual(256, bounded.Entries.Count);
        AssertTrue(bounded.Dropped > 0);
        AssertTrue(snapshot.Entries.Count < bounded.Entries.Count);
        AssertFalse(bounded.Entries.Any(entry => entry.ToString().Contains(secret, StringComparison.Ordinal)));

        var queries = new XsrQueryRouterBuilder();
        trace.Register(queries);
        var queryRouter = queries.Build(RuntimeTraceSession.Combine((IXsrDispatchObserver)existing, trace));
        AssertTrue(queryRouter.TryResolve(RuntimeTraceContract.Read, out var query));
        var captured = await queryRouter.QueryAsync<RuntimeTraceQuery, RuntimeTraceSnapshot>(query, new());
        AssertTrue(captured.IsSuccess);
        AssertEqual(256, captured.Value!.Entries.Count);
        var retained = captured.Value.Entries[^1];
        store.Publish(store.Resolve(stateKey), new TraceSecret(secret));
        AssertEqual(retained, captured.Value.Entries[^1]);
    }

    private sealed record TraceSecret(string Value);
    private sealed class ExistingTraceObserver : IXsrDispatchObserver, IXsrStateObserver, IXsrEventObserver,
        IXsrSchedulerObserver, IXsrLifecycleObserver
    {
        internal int Starts, Completions, States, Events, Transitions;
        internal TaskCompletionSource Scheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnStarted(XsrDispatchStarted observation) { Interlocked.Increment(ref Starts); throw new InvalidOperationException("existing observer failure"); }
        public void OnCompleted(XsrDispatchObservation observation) { Interlocked.Increment(ref Completions); throw new InvalidOperationException("existing observer failure"); }
        public void OnChanged(XsrStateChange change) { Interlocked.Increment(ref States); throw new InvalidOperationException("existing observer failure"); }
        public void OnPublished(XsrEventPublication publication) { Interlocked.Increment(ref Events); throw new InvalidOperationException("existing observer failure"); }
        public void OnExecuted(XsrScheduledObservation observation) { Scheduled.TrySetResult(); throw new InvalidOperationException("existing observer failure"); }
        public void OnPhaseChanged(XsrLifecycleTransition transition) { Interlocked.Increment(ref Transitions); throw new InvalidOperationException("existing observer failure"); }
    }
}
