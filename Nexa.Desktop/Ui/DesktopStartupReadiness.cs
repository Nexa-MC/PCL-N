namespace Nexa.Desktop.Ui;

/// <summary>A finite attempt: workers with application lifetimes are never readiness steps.</summary>
internal sealed class DesktopStartupReadiness(Action<string> report, CancellationToken cancellationToken)
{
    private readonly Dictionary<string, Task> _steps = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    internal static readonly TimeSpan LocalBudget = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan OnlineBudget = TimeSpan.FromSeconds(8);

    internal Task RunAsync(string name, Func<CancellationToken, Task> prepare,
        TimeSpan? budget = null, Func<Task>? onTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(prepare);
        lock (_gate)
        {
            if (_steps.TryGetValue(name, out Task? existing)) return existing;
            Task task = RunStepAsync(name, prepare, budget ?? LocalBudget, onTimeout);
            _steps.Add(name, task);
            return task;
        }
    }

    private async Task RunStepAsync(string name, Func<CancellationToken, Task> prepare,
        TimeSpan budget, Func<Task>? onTimeout)
    {
        if (budget <= TimeSpan.Zero || budget == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(budget));
        cancellationToken.ThrowIfCancellationRequested();
        report(name);
        using CancellationTokenSource step = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task work = prepare(step.Token);
        try { await work.WaitAsync(budget, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            await step.CancelAsync().ConfigureAwait(false);
            // Observe an uncancellable provider's eventual fault without blocking readiness.
            _ = work.ContinueWith(static completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            if (onTimeout is null) throw new TimeoutException($"Startup initialization timed out: {name}.");
            await onTimeout().ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            await step.CancelAsync().ConfigureAwait(false);
            if (onTimeout is null)
                throw new IOException($"Startup initialization was canceled before readiness: {name}.", error);
            await onTimeout().ConfigureAwait(false);
        }
    }
}
