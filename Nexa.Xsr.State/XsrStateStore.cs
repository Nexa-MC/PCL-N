namespace Nexa.Xsr.State;

/// <summary>
/// The immutable-topology, revisioned state store. Reads pull coherent applied state; writers
/// assign the next revision per entry. Derived entries recompute only after an input revision
/// changes, and availability is carried separately from the last value.
/// </summary>
public sealed class XsrStateStore
{
    private readonly XsrRegistrySnapshot<XsrStateDescriptor> _registry;
    private XsrStateNode[] _nodes;
    private readonly IXsrStateObserver? _observer;
    private long _changeStamp;
    private readonly object _initialCommitGate = new();
    // Zero admits initial commit; ordinary mutation permanently closes that option. Readers
    // use the node table directly and never take this gate.
    private int _initialCommitPhase;
    private const int OrdinaryMutationPhase = 1;
    private const int InitialCommitPhase = 2;
    private const int InitialCommittedPhase = 3;
    private const int MaximumInitialCells = 4096;
    private const long MaximumInitialByteArrayBytes = 32 * 1024 * 1024;

    /// <summary>Applied changes only. Subscribers queue work; callbacks may run on any publisher thread.</summary>
    public event Action<XsrStateChange>? Changed;

    private readonly Dictionary<XsrStateId, List<XsrStateId>> _derivedDependents;

    internal XsrStateStore(
        XsrRegistrySnapshot<XsrStateDescriptor> registry,
        XsrStateNode[] nodes,
        IXsrStateObserver? observer)
    {
        _registry = registry;
        _nodes = nodes;
        _observer = observer;

        _derivedDependents = [];
        foreach (XsrRegistryEntry<XsrStateDescriptor> entry in registry.Entries)
        {
            if (nodes[entry.RuntimeId.Value - 1] is IXsrStateDerivedNode derived)
            {
                foreach (XsrStateId dependency in derived.DependencyIds)
                {
                    if (!_derivedDependents.TryGetValue(dependency, out List<XsrStateId>? dependents))
                    {
                        dependents = [];
                        _derivedDependents[dependency] = dependents;
                    }

                    dependents.Add(new XsrStateId(entry.RuntimeId));
                }
            }
        }
    }

    public int Count => _registry.Count;

    /// <summary>
    /// Commits one complete primitive-cell snapshot into a previously unpublished store. Store
    /// identity and subscriptions survive; this is a one-time initialization, not a transaction.
    /// </summary>
    public void CommitInitialSnapshot(XsrStateStore candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (ReferenceEquals(this, candidate))
            throw new InvalidOperationException("The initial snapshot must be a separate candidate store.");
        XsrStateChange[] changes;
        lock (_initialCommitGate)
        {
            if (Interlocked.CompareExchange(ref _initialCommitPhase, InitialCommitPhase, 0) != 0)
                throw new InvalidOperationException("The store has already admitted a writer or initial snapshot.");
            try
            {
                XsrStateNode[] original = Volatile.Read(ref _nodes);
                XsrStateNode[] source = Volatile.Read(ref candidate._nodes);
                if (original.Length != source.Length || original.Length > MaximumInitialCells)
                    throw new InvalidOperationException("The initial snapshot topology or item budget does not match.");
                XsrStateNode[] owned = new XsrStateNode[original.Length];
                changes = new XsrStateChange[original.Length];
                long remainingBytes = MaximumInitialByteArrayBytes;
                for (int index = 0; index < original.Length; index++)
                {
                    XsrStateNode target = original[index];
                    XsrStateNode value = source[index];
                    if (target is not IXsrStateCellNode cell || !cell.IsUnpublished
                        || target.SemanticId != value.SemanticId || target.RuntimeId != value.RuntimeId
                        || target.Descriptor != value.Descriptor)
                        throw new InvalidOperationException("The initial snapshot requires matching unpublished cell topology and ownership.");
                    owned[index] = cell.CloneInitialFrom(value, NextChangeStamp(), ref remainingBytes);
                    changes[index] = new(new(target.RuntimeId), target.SemanticId, XsrStateKind.Cell,
                        1, XsrStateAvailability.Available, XsrStateChangeReason.ValuePublished);
                }
                Volatile.Write(ref _nodes, owned);
                Volatile.Write(ref _initialCommitPhase, InitialCommittedPhase);
            }
            catch
            {
                Volatile.Write(ref _initialCommitPhase, 0);
                throw;
            }
        }
        foreach (XsrStateChange change in changes) Notify(change);
    }

    public bool TryResolve(XsrSemanticId semanticId, out XsrStateId stateId)
    {
        if (_registry.TryGetRuntimeId(semanticId, out XsrRuntimeId runtimeId))
        {
            stateId = new XsrStateId(runtimeId);
            return true;
        }

        stateId = default;
        return false;
    }

    /// <summary>
    /// Resolves a declared semantic identifier to its state identifier, rejecting undeclared input.
    /// </summary>
    public XsrStateId Resolve(XsrSemanticId semanticId) =>
        TryResolve(semanticId, out XsrStateId stateId)
            ? stateId
            : throw new InvalidOperationException($"The XSR state '{semanticId}' is not registered.");

    public XsrStateDescriptor Describe(XsrStateId stateId) => RequireNode(stateId).Descriptor;

    /// <summary>
    /// Allocates the next store-global change stamp. Stamps are strictly monotonic, so any
    /// applied mutation raises the stamp of exactly the entry it mutated.
    /// </summary>
    internal long NextChangeStamp() => Interlocked.Increment(ref _changeStamp);

    /// <summary>
    /// Reads one typed cell, applying any deferred coalesced publication first.
    /// </summary>
    public XsrStateValue<TValue> Read<TValue>(XsrStateId stateId, CancellationToken cancellationToken = default)
    {
        XsrStateNode node = RequireNode(stateId);

        if (node is XsrStateCellNode<TValue> cell)
        {
            XsrStateValue<TValue> value = cell.Read(
                stateId,
                NextChangeStamp(),
                cancellationToken,
                out XsrStateChange? flushed);
            Notify(flushed);
            return value;
        }

        if (node is XsrStateDerivedNode<TValue> derived)
        {
            XsrStateValue<TValue> value = derived.Read(this, stateId, cancellationToken, out XsrStateChange? change);
            Notify(change);
            return value;
        }

        throw MismatchedContract(stateId, node, typeof(TValue));
    }

    /// <summary>
    /// Publishes one typed cell value immediately, assigning the next revision. Any deferred
    /// coalesced publication is applied first, in publication order.
    /// </summary>
    public long Publish<TValue>(XsrStateId stateId, TValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AdmitOrdinaryMutation();
        XsrStateNode node = RequireNode(stateId);

        if (node is not XsrStateCellNode<TValue> cell)
        {
            throw MismatchedContract(stateId, node, typeof(TValue));
        }

        XsrStateChange change = cell.Publish(
            stateId,
            value,
            NextChangeStamp(),
            NextChangeStamp(),
            out XsrStateChange? flushed);
        Notify(flushed);
        Notify(change);
        return change.Revision;
    }

    /// <summary>
    /// Publishes one replaceable cell value with latest-wins coalescing. The value becomes visible
    /// with one revision at the next read or snapshot capture; replaced intermediate publications
    /// are counted in <see cref="CoalescedCount"/>.
    /// </summary>
    public void PublishCoalesced<TValue>(XsrStateId stateId, TValue value)
    {
        AdmitOrdinaryMutation();
        XsrStateNode node = RequireNode(stateId);

        if (node is not XsrStateCellNode<TValue> cell)
        {
            throw MismatchedContract(stateId, node, typeof(TValue));
        }

        cell.PublishCoalesced(value);
        Notify(new XsrStateChange(
            stateId,
            node.SemanticId,
            XsrStateKind.Cell,
            node.Revision,
            XsrStateAvailability.Unavailable,
            XsrStateChangeReason.CoalescedPublished));
    }

    /// <summary>
    /// Reports how many coalesced publications were replaced before they became a revision.
    /// </summary>
    public long CoalescedCount(XsrStateId stateId) => RequireNode(stateId).CoalescedCount;

    /// <summary>
    /// Reads the applied value of one entry boxed. Cells flush deferred coalesced publications;
    /// derived entries recompute when their dependencies changed. Typed hot paths use
    /// <see cref="Read{TValue}"/>.
    /// </summary>
    public object? ReadAppliedValue(XsrStateId stateId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        XsrStateNode node = RequireNode(stateId);

        if (node is IXsrStateCellNode cell)
        {
            object? value = cell.ReadApplied(stateId, NextChangeStamp(), out XsrStateChange? flushed);
            Notify(flushed);
            return value;
        }

        if (node is IXsrStateDerivedNode derived)
        {
            object? value = derived.ReadAppliedObject(this, stateId, cancellationToken, out XsrStateChange? change);
            Notify(change);
            return value;
        }

        throw MismatchedContract(stateId, node, typeof(object));
    }

    /// <summary>
    /// Resolves every state entry whose applied value can change when one entry changes: the
    /// entry itself plus all derived entries that transitively depend on it.
    /// </summary>
    public IReadOnlyList<XsrStateId> AffectedBy(XsrStateId changed)
    {
        if (!_derivedDependents.TryGetValue(changed, out List<XsrStateId>? direct))
        {
            return [changed];
        }

        List<XsrStateId> affected = [changed];
        HashSet<XsrStateId> visited = [changed];
        for (int index = 0; index < affected.Count; index++)
        {
            if (_derivedDependents.TryGetValue(affected[index], out List<XsrStateId>? next))
            {
                foreach (XsrStateId dependent in next)
                {
                    if (visited.Add(dependent))
                    {
                        affected.Add(dependent);
                    }
                }
            }
        }

        return affected;
    }

    /// <summary>
    /// Reads one ordered collection snapshot. The returned items never change after capture.
    /// </summary>
    public XsrCollectionSnapshot<TItem> ReadCollection<TItem>(
        XsrStateId stateId,
        CancellationToken cancellationToken = default)
    {
        XsrStateNode node = RequireNode(stateId);

        if (node is not IXsrStateCollectionNode collection)
        {
            throw MismatchedContract(stateId, node, typeof(TItem));
        }

        return collection.ReadAs<TItem>(stateId, cancellationToken);
    }

    /// <summary>
    /// Applies one collection delta. When the delta base revision no longer matches, the store
    /// rejects the delta without mutation and the caller refreshes a snapshot.
    /// </summary>
    public XsrCollectionApplyResult PublishDelta<TItem, TKey>(
        XsrStateId stateId,
        XsrCollectionDelta<TItem, TKey> delta,
        CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(delta);
        cancellationToken.ThrowIfCancellationRequested();
        AdmitOrdinaryMutation();
        XsrStateNode node = RequireNode(stateId);

        if (node is not XsrStateCollectionNode<TItem, TKey> collection)
        {
            throw MismatchedContract(stateId, node, typeof(TItem));
        }

        XsrCollectionApplyResult result = collection.PublishDelta(
            stateId,
            delta,
            NextChangeStamp(),
            out XsrStateChange? change);
        Notify(change);
        return result;
    }

    /// <summary>
    /// Marks entry availability without touching its value. Remote outages mark mirrors stale or
    /// unavailable while retaining the last value.
    /// </summary>
    public bool MarkAvailability(XsrStateId stateId, XsrStateAvailability availability)
    {
        AdmitOrdinaryMutation();
        XsrStateNode node = RequireNode(stateId);

        if (node is IXsrStateDerivedNode)
        {
            throw new InvalidOperationException(
                $"Derived state '{stateId}' derives availability from its dependencies.");
        }

        bool changed = node.SetAvailability(availability, NextChangeStamp(), out XsrStateChange? change);
        Notify(change);
        return changed;
    }

    /// <summary>
    /// Captures one whole-store snapshot. Deferred coalesced publications are applied first, and
    /// entries are ordered by runtime ID.
    /// </summary>
    public XsrStateSnapshot CaptureSnapshot(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        XsrStateNode[] nodes = Volatile.Read(ref _nodes);
        XsrStateSnapshotEntry[] entries = new XsrStateSnapshotEntry[nodes.Length];
        for (int index = 0; index < nodes.Length; index++)
        {
            XsrStateId stateId = new(nodes[index].RuntimeId);
            FlushNode(nodes[index], stateId);
            entries[index] = nodes[index].Capture(stateId);
        }

        return new XsrStateSnapshot(entries);
    }

    internal (long Revision, XsrStateAvailability Availability)[] CaptureDependencies(
        IReadOnlyList<XsrStateId> dependencies, CancellationToken token)
    {
        var versions = new (long Revision, XsrStateAvailability Availability)[dependencies.Count];
        for (int i = 0; i < dependencies.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            XsrStateId id = dependencies[i];
            XsrStateNode node = RequireNode(id);
            FlushNode(node, id);
            if (node is IXsrStateDerivedNode derived)
            {
                derived.ReadAppliedObject(this, id, token, out var change);
                Notify(change);
            }
            var snapshot = node.Capture(id);
            versions[i] = (snapshot.Revision, snapshot.Availability);
        }
        return versions;
    }

    internal void FlushNode(XsrStateNode node, XsrStateId stateId)
    {
        if (node is IXsrStateDerivedNode derived)
        {
            foreach (XsrStateId dependency in derived.DependencyIds)
            {
                FlushNode(RequireNode(dependency), dependency);
            }

            return;
        }

        node.ApplyPending(stateId, NextChangeStamp(), out XsrStateChange? flushed);
        Notify(flushed);
    }

    internal XsrStateNode RequireNode(XsrStateId stateId)
    {
        XsrStateNode[] nodes = Volatile.Read(ref _nodes);
        if (!stateId.IsAssigned || stateId.Value.Value > (uint)nodes.Length)
        {
            throw new ArgumentException($"The XSR state identifier '{stateId}' is not registered.", nameof(stateId));
        }

        return nodes[(int)stateId.Value.Value - 1];
    }

    private void AdmitOrdinaryMutation()
    {
        while (true)
        {
            int phase = Volatile.Read(ref _initialCommitPhase);
            if (phase is OrdinaryMutationPhase or InitialCommittedPhase) return;
            if (phase == 0)
            {
                if (Interlocked.CompareExchange(ref _initialCommitPhase, OrdinaryMutationPhase, 0) == 0) return;
                continue;
            }
            // Initial cloning has a fixed cell/byte budget and invokes no user code. Wait only
            // on this cold path, then resolve the published node generation for the write.
            lock (_initialCommitGate) { }
        }
    }

    private static InvalidOperationException MismatchedContract(
        XsrStateId stateId,
        XsrStateNode node,
        Type requested) =>
        new(
            $"State '{node.SemanticId}' ({stateId}) is a {node.Kind} owned by '{node.Descriptor.Owner}' "
            + $"and does not match the requested contract '{requested.Name}'.");

    private void Notify(XsrStateChange? change)
    {
        if (change is not { } observed)
        {
            return;
        }

        try
        {
            _observer?.OnChanged(observed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // State publication must not be changed by a diagnostics observer failure.
        }
        if (Changed is { } subscribers)
            foreach (Action<XsrStateChange> subscriber in subscribers.GetInvocationList())
                try { subscriber(observed); }
                catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { }
    }
}
