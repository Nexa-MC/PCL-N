using Nexa.Xsr.State;

namespace Nexa.Services.Scheduling;

/// <summary>Bounded admission without worker threads, polling or per-service timers.</summary>
public sealed class WorkScheduler : IWorkScheduler, IDisposable
{
    private static readonly int[] Weights = [8, 4, 2, 1];
    private readonly object _gate = new();
    private readonly object _publicationGate = new();
    private readonly AsyncLocal<WorkPriority?> _priority = new();
    private readonly LinkedList<Waiter>[] _queues = new LinkedList<Waiter>[4];
    private readonly int[] _limits;
    private readonly int[] _active = new int[3];
    private int _cursor;
    private int _remaining = 8;
    private readonly HashSet<QuietLease> _quiet = [];
    private readonly int _capacity;
    private readonly TimeProvider _clock;
    private readonly XsrStateStore? _store;
    private int _waiting;
    private long _revision;
    private long _publishedRevision = -1;
    private bool _disposed;

    public WorkScheduler(WorkSchedulerOptions? options = null, XsrStateStore? store = null, TimeProvider? clock = null)
    {
        options ??= new();
        foreach (int limit in new[] { options.CpuConcurrency, options.DiskConcurrency, options.HttpConcurrency })
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (options.CpuConcurrency > 64 || options.DiskConcurrency > 64 || options.HttpConcurrency > 64
            || options.QueueCapacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(options));
        _limits = [options.CpuConcurrency, options.DiskConcurrency, options.HttpConcurrency];
        _capacity = options.QueueCapacity; _store = store; _clock = clock ?? TimeProvider.System;
        for (int priority = 0; priority < 4; priority++) _queues[priority] = new();
        PublishQuiet();
    }

    public WorkPriority CurrentPriority => _priority.Value ?? WorkPriority.Interactive;
    public IDisposable UsePriority(WorkPriority priority)
    {
        Validate(priority);
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        return new PriorityScope(this, priority);
    }

    public ValueTask<IDisposable> AcquireAsync(WorkPriority priority, WorkResource resource, CancellationToken token = default)
    {
        Validate(priority);
        if ((uint)resource is 0 or > 7) throw new ArgumentOutOfRangeException(nameof(resource));
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (((int)priority < (int)WorkPriority.Background || _quiet.Count == 0) && CanAdmit(resource) && !HasRunnableWaiting())
            {
                CountActive(resource, 1);
                return ValueTask.FromResult<IDisposable>(new ResourceLease(this, resource));
            }
            int reserved = Math.Min(32, _capacity / 4);
            if (_waiting >= _capacity || (int)priority >= (int)WorkPriority.Background && _waiting >= _capacity - reserved)
                throw new InvalidOperationException("Host resource admission queue is full.");
            Waiter waiter = new(this, priority, resource, token);
            waiter.Node = _queues[(int)priority].AddLast(waiter);
            _waiting++;
            waiter.Registration = token.UnsafeRegister(static state =>
            {
                var item = (Waiter)state!;
                item.Owner.Cancel(item);
            }, waiter);
            // Registration may synchronously cancel an already queued waiter.
            if (waiter.Node is null) _ = waiter.Registration.Unregister();
            Pump();
            return new(waiter.Completion.Task);
        }
    }

    public IDisposable? TryAcquire(WorkPriority priority, WorkResource resource, CancellationToken token = default)
    {
        Validate(priority);
        if ((uint)resource is 0 or > 7) throw new ArgumentOutOfRangeException(nameof(resource));
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((int)priority >= (int)WorkPriority.Background && _quiet.Count != 0
                || !CanAdmit(resource) || HasRunnableWaiting()) return null;
            CountActive(resource, 1);
            return new ResourceLease(this, resource);
        }
    }

    public IWorkQuietLease EnterQuiet()
    {
        QuietLease lease;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lease = new(this, _clock); _quiet.Add(lease); _revision++;
        }
        PublishQuiet();
        return lease;
    }

    public WorkSchedulerSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                WorkResourceSnapshot[] resources = new WorkResourceSnapshot[3];
                for (int r = 0; r < 3; r++)
                {
                    int waiting = 0;
                    WorkResource resource = (WorkResource)(1 << r);
                    for (int p = 0; p < 4; p++)
                        foreach (Waiter waiter in _queues[p]) if ((waiter.Resource & resource) != 0) waiting++;
                    resources[r] = new(resource, _active[r], waiting);
                }
                return new(_disposed, _quiet.Count, Array.AsReadOnly(resources));
            }
        }
    }

    private void Cancel(Waiter waiter)
    {
        lock (_gate)
        {
            if (waiter.Node is null) return;
            Remove(waiter);
            waiter.Completion.TrySetCanceled(waiter.Token);
            Pump();
        }
    }

    private void Remove(Waiter waiter)
    {
        _queues[(int)waiter.Priority].Remove(waiter.Node!);
        waiter.Node = null; _waiting--;
        // Dispose can wait for a callback that is itself waiting for _gate. Unregister cannot.
        _ = waiter.Registration.Unregister();
    }

    private bool CanAdmit(WorkResource resource)
    {
        for (int r = 0; r < 3; r++)
            if (((int)resource & (1 << r)) != 0 && _active[r] >= _limits[r]) return false;
        return true;
    }

    private bool HasRunnableWaiting()
    {
        for (int p = 0; p < (_quiet.Count == 0 ? 4 : 2); p++)
            foreach (Waiter waiter in _queues[p]) if (CanAdmit(waiter.Resource)) return true;
        return false;
    }

    private void CountActive(WorkResource resource, int delta)
    {
        for (int r = 0; r < 3; r++) if (((int)resource & (1 << r)) != 0) _active[r] += delta;
    }

    private void Pump()
    {
        while (!_disposed)
        {
            // Saturated resources and quiet-only queues must not consume a service quantum.
            if (!HasRunnableWaiting()) return;
            Waiter? selected = null;
            for (int visited = 0; visited < 5; visited++)
            {
                int priority = _cursor;
                if ((priority < 2 || _quiet.Count == 0) && _remaining > 0)
                {
                    foreach (Waiter waiter in _queues[priority])
                        if (CanAdmit(waiter.Resource)) { selected = waiter; break; }
                }
                if (selected is not null) { _remaining--; break; }
                _cursor = (priority + 1) % 4;
                _remaining = Weights[_cursor];
            }
            if (selected is null) return;
            Remove(selected); CountActive(selected.Resource, 1);
            selected.Completion.TrySetResult(new ResourceLease(this, selected.Resource));
        }
    }

    private void Release(WorkResource resource)
    {
        lock (_gate) { CountActive(resource, -1); Pump(); }
    }

    private void LeaveQuiet(QuietLease lease)
    {
        lock (_gate)
        {
            if (!_quiet.Remove(lease)) return;
            _revision++;
            Pump();
        }
        PublishQuiet();
    }

    private void PublishQuiet()
    {
        if (_store is null) return;
        lock (_publicationGate)
        {
            WorkQuietSnapshot snapshot;
            lock (_gate)
            {
                if (_publishedRevision >= _revision) return;
                snapshot = new(_quiet.Count != 0, _quiet.Count, _revision);
                _publishedRevision = _revision;
            }
            _store.Publish(_store.Resolve(WorkSchedulingContract.QuietKey), snapshot);
        }
    }

    public void Dispose()
    {
        QuietLease[] scopes;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; scopes = [.. _quiet];
            for (int p = 0; p < 4; p++)
                while (_queues[p].First is { } node)
                {
                    Waiter waiter = node.Value; Remove(waiter);
                    waiter.Completion.TrySetException(new ObjectDisposedException(nameof(WorkScheduler)));
                }
        }
        foreach (QuietLease scope in scopes) scope.Dispose();
    }

    private static void Validate(WorkPriority priority)
    {
        if ((uint)priority > 3) throw new ArgumentOutOfRangeException(nameof(priority));
    }

    private sealed class Waiter(WorkScheduler owner, WorkPriority priority, WorkResource resource, CancellationToken token)
    {
        public WorkScheduler Owner { get; } = owner;
        public WorkPriority Priority { get; } = priority;
        public WorkResource Resource { get; } = resource;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
    }
    private sealed class ResourceLease(WorkScheduler owner, WorkResource resource) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(resource); }
    }
    private sealed class PriorityScope : IDisposable
    {
        private readonly WorkScheduler _owner;
        private readonly WorkPriority? _previous;
        private int _disposed;
        public PriorityScope(WorkScheduler owner, WorkPriority priority)
        { _owner = owner; _previous = owner._priority.Value; owner._priority.Value = priority; }
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner._priority.Value = _previous; }
    }
    private sealed class QuietLease(WorkScheduler owner, TimeProvider clock) : IWorkQuietLease
    {
        private readonly object _gate = new();
        private ITimer? _timer;
        private bool _disposed;
        public void ReleaseAfter(TimeSpan gracePeriod)
        {
            if (gracePeriod < TimeSpan.Zero || gracePeriod > TimeSpan.FromMinutes(2))
                throw new ArgumentOutOfRangeException(nameof(gracePeriod));
            lock (_gate)
            {
                if (_disposed || _timer is not null) return;
                _timer = clock.CreateTimer(static state => ((QuietLease)state!).Dispose(), this,
                    Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _ = _timer.Change(gracePeriod, Timeout.InfiniteTimeSpan);
            }
        }
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true; _timer?.Dispose(); _timer = null;
            }
            owner.LeaveQuiet(this);
        }
    }
}
