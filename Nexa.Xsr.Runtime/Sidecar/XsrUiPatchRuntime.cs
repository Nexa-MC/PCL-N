using Nexa.Sidecar.Protocol;
using Nexa.Xsr.State;

namespace Nexa.Xsr.Runtime;

public sealed record XsrUiCaptionTarget(XsrSemanticId Target, int MaximumCharacters = 64);

/// <summary>Immutable sealed projection, indexed by the host catalog rather than plugin entity names.</summary>
public sealed class XsrUiPatchSnapshot
{
    private readonly string?[] _captions;
    internal XsrUiPatchSnapshot(string?[] captions) => _captions = captions;
    public string? CaptionAt(int index) => index >= 0 && index < _captions.Length ? _captions[index] : null;
}

public sealed class XsrUiPatchAdmission
{
    internal XsrUiPatchRuntime Runtime { get; }
    private readonly HashSet<XsrSemanticId> _allowed;
    public XsrUiPatchAdmission(XsrUiPatchRuntime runtime, params XsrSemanticId[] targets)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(targets);
        _allowed = new(targets);
        foreach (var target in targets) _ = runtime.Resolve(target);
    }

    internal XsrPreparedCaption[] Prepare(IReadOnlyList<SidecarExtensionRegistration> entries)
    {
        List<XsrPreparedCaption> patches = [];
        foreach (var entry in entries)
        {
            if (entry.Kind != SidecarRegistrationKind.UiPatch) continue;
            if (entry.Flags != 0 || !_allowed.Contains(entry.Target))
                throw new SidecarProtocolException("UI patch target or flags are not granted by the host.");
            int index = Runtime.Resolve(entry.Target);
            string caption = SidecarUiCaptionPatch.Decode(entry.Payload.Span);
            if (caption.Length > Runtime.MaximumCharacters(index))
                throw new SidecarProtocolException("UI caption exceeds the host target budget.");
            patches.Add(new(index, caption));
            if (patches.Count > 128) throw new SidecarProtocolException("Session UI patch budget exceeded.");
        }
        return patches.ToArray();
    }
}

internal sealed record XsrPreparedCaption(int Index, string Caption);

/// <summary>Publishes local caption patches as State; observers never run under its business lock.</summary>
public sealed class XsrUiPatchRuntime
{
    private readonly object _gate = new();
    private readonly XsrStateStore _store;
    private readonly XsrStateId _state;
    private readonly Dictionary<XsrSemanticId, int> _indices = [];
    private readonly XsrUiCaptionTarget[] _targets;
    private ActiveCaption[][] _table;
    private XsrUiPatchSnapshot? _pending;
    private bool _publishing;

    public XsrUiPatchRuntime(XsrStateStore store, XsrStateId state, params XsrUiCaptionTarget[] targets)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _state = state;
        _ = store.Read<XsrUiPatchSnapshot>(state);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targets.Length, 64);
        _targets = targets.ToArray();
        _table = new ActiveCaption[targets.Length][];
        for (int i = 0; i < targets.Length; i++)
        {
            _ = XsrSemanticId.Parse(targets[i].Target.Value);
            if (targets[i].MaximumCharacters is < 1 or > 256) throw new ArgumentException("Invalid caption character budget.", nameof(targets));
            _indices.Add(targets[i].Target, i);
            _table[i] = [];
        }
    }

    public int Resolve(XsrSemanticId target) => _indices.TryGetValue(target, out int index) ? index
        : throw new ArgumentException("Unknown host UI caption target.", nameof(target));
    internal int MaximumCharacters(int index) => _targets[index].MaximumCharacters;

    internal CaptionLease Activate(XsrPreparedCaption[] prepared)
    {
        var lease = new CaptionLease(this);
        bool publish;
        lock (_gate)
        {
            var table = _table.Select(items => items.ToList()).ToArray();
            foreach (var patch in prepared) table[patch.Index].Add(new(lease, patch.Caption));
            if (table.Sum(items => items.Count) > 256 || table.Any(items => items.Count > 32))
                throw new SidecarProtocolException("Active UI caption budget exceeded.");
            _table = table.Select(items => items.ToArray()).ToArray();
            publish = QueueSnapshot();
        }
        lease.Publishes = publish;
        return lease;
    }

    private void Retire(CaptionLease lease)
    {
        bool publish;
        lock (_gate)
        {
            _table = _table.Select(items => items.Where(item => item.Lease != lease).ToArray()).ToArray();
            publish = QueueSnapshot();
        }
        if (publish) PublishPending();
    }

    private bool QueueSnapshot()
    {
        _pending = new(_table.Select(items => items.LastOrDefault()?.Caption).ToArray());
        if (_publishing) return false;
        _publishing = true;
        return true;
    }

    private void PublishPending()
    {
        while (true)
        {
            XsrUiPatchSnapshot snapshot;
            lock (_gate)
            {
                if (_pending is null) { _publishing = false; return; }
                snapshot = _pending;
                _pending = null;
            }
            try { _store.Publish(_state, snapshot); }
            catch (ObjectDisposedException)
            { lock (_gate) { _pending = null; _publishing = false; } return; }
        }
    }

    private sealed record ActiveCaption(CaptionLease Lease, string Caption);
    internal sealed class CaptionLease(XsrUiPatchRuntime owner) : IDisposable
    {
        private int _retired;
        internal bool Publishes { get; set; }
        internal void Publish() { if (Publishes) owner.PublishPending(); }
        public void Dispose() { if (Interlocked.Exchange(ref _retired, 1) == 0) owner.Retire(this); }
    }
}
