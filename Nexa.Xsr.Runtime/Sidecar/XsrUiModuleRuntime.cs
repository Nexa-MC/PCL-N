using Nexa.Sidecar.Protocol;
using Nexa.Xsr.State;

namespace Nexa.Xsr.Runtime;

public sealed class XsrUiModuleSnapshot
{
    private readonly SidecarUiCard?[] _cards;
    internal XsrUiModuleSnapshot(SidecarUiCard?[] cards) => _cards = cards;
    public SidecarUiCard? CardAt(int index) => index >= 0 && index < _cards.Length ? _cards[index] : null;
}

public sealed class XsrUiModuleAdmission
{
    internal XsrUiModuleRuntime Runtime { get; }
    private readonly HashSet<XsrSemanticId> _allowed;
    public XsrUiModuleAdmission(XsrUiModuleRuntime runtime, params XsrSemanticId[] slots)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(slots);
        _allowed = new(slots);
        foreach (var slot in slots) _ = runtime.Resolve(slot);
    }

    internal XsrPreparedModule[] Prepare(IEnumerable<SidecarRegistrationItem> entries)
    {
        List<XsrPreparedModule> modules = [];
        foreach (var entry in entries)
        {
            if (entry.Kind != SidecarRegistrationKind.UiModule) continue;
            if (entry.Flags != 0 || entry.CodecId != 0 || !string.IsNullOrEmpty(entry.RequiredResources))
                throw new SidecarProtocolException("UI card flags, codec and resources are not granted.");
            var card = SidecarUiCard.Decode(entry.Payload ?? []);
            var slot = XsrSemanticId.Parse(card.Slot);
            if (!_allowed.Contains(slot)) throw new SidecarProtocolException("UI module slot is not granted by the host.");
            modules.Add(new(Runtime.Resolve(slot), card));
            if (modules.Count > 32) throw new SidecarProtocolException("Session UI module budget exceeded.");
        }
        return modules.ToArray();
    }
}

internal sealed record XsrPreparedModule(int Index, SidecarUiCard Card);

/// <summary>Publishes admitted documents to State, without invoking observers under its lock.</summary>
public sealed class XsrUiModuleRuntime
{
    private readonly object _gate = new();
    private readonly XsrStateStore _store;
    private readonly XsrStateId _state;
    private readonly Dictionary<XsrSemanticId, int> _indices = [];
    private ActiveModule[][] _table;
    private XsrUiModuleSnapshot? _pending;
    private bool _publishing;

    public XsrUiModuleRuntime(XsrStateStore store, XsrStateId state, params XsrSemanticId[] slots)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _state = state;
        _ = store.Read<XsrUiModuleSnapshot>(state);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slots.Length, 16);
        _table = new ActiveModule[slots.Length][];
        for (int i = 0; i < slots.Length; i++)
        {
            _ = XsrSemanticId.Parse(slots[i].Value);
            _indices.Add(slots[i], i);
            _table[i] = [];
        }
    }

    public int Resolve(XsrSemanticId slot) => _indices.TryGetValue(slot, out int index) ? index
        : throw new ArgumentException("Unknown host UI module slot.", nameof(slot));

    internal ModuleLease Activate(XsrPreparedModule[] prepared)
    {
        var lease = new ModuleLease(this);
        lock (_gate)
        {
            var table = _table.Select(items => items.ToList()).ToArray();
            foreach (var module in prepared) table[module.Index].Add(new(lease, module.Card));
            if (table.Sum(items => items.Count) > 64 || table.Any(items => items.Count > 8))
                throw new SidecarProtocolException("Active UI module budget exceeded.");
            _table = table.Select(items => items.ToArray()).ToArray();
            lease.Publishes = QueueSnapshot();
        }
        return lease;
    }

    private void Retire(ModuleLease lease)
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
        _pending = new(_table.Select(items => items.LastOrDefault()?.Card).ToArray());
        if (_publishing) return false;
        _publishing = true;
        return true;
    }

    private void PublishPending()
    {
        while (true)
        {
            XsrUiModuleSnapshot snapshot;
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

    private sealed record ActiveModule(ModuleLease Lease, SidecarUiCard Card);
    internal sealed class ModuleLease(XsrUiModuleRuntime owner) : IDisposable
    {
        private int _retired;
        internal bool Publishes { get; set; }
        internal void Publish() { if (Publishes) owner.PublishPending(); }
        public void Dispose() { if (Interlocked.Exchange(ref _retired, 1) == 0) owner.Retire(this); }
    }
}
