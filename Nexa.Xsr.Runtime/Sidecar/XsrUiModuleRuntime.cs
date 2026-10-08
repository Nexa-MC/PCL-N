using System.Globalization;
using Nexa.Core.Media;
using Nexa.Sidecar.Protocol;
using Nexa.Xsr.State;

namespace Nexa.Xsr.Runtime;

public sealed class XsrUiModuleSnapshot
{
    private readonly XsrUiModuleView?[] _modules;
    internal XsrUiModuleSnapshot(XsrUiModuleView?[] modules) => _modules = modules;
    public SidecarUiCard? CardAt(int index) => ModuleAt(index)?.Card;
    public XsrUiModuleView? ModuleAt(int index) => index >= 0 && index < _modules.Length ? _modules[index] : null;
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

    internal XsrPreparedModule[] Prepare(IEnumerable<SidecarRegistrationItem> entries, SidecarRegistrationSet registration, SidecarStateMirror mirror, SidecarHostCache cache, SidecarFeatures features)
    {
        List<XsrPreparedModule> modules = [];
        Dictionary<string, PngImage> images = new(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Kind != SidecarRegistrationKind.UiModule) continue;
            if (entry.Flags != 0 || entry.CodecId != 0)
                throw new SidecarProtocolException("UI module flags and codec are not granted.");
            var payload = entry.Payload ?? [];
            bool interactive = SidecarUiDocument.ReadSchema(payload) == 2;
            var document = interactive ? SidecarUiDocument.Decode(payload) : null;
            if (!interactive && !string.IsNullOrEmpty(entry.RequiredResources))
                throw new SidecarProtocolException("Text card resource dependencies are not granted.");
            var card = document?.Card ?? SidecarUiCard.Decode(payload);
            var slot = XsrSemanticId.Parse(card.Slot);
            if (!_allowed.Contains(slot)) throw new SidecarProtocolException("UI module slot is not granted by the host.");
            var resources = (entry.RequiredResources ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            var nodes = document?.Nodes.Select(node => PrepareNode(node, registration, mirror, cache, resources, images)).ToArray() ?? [];
            if (!features.HasFlag(SidecarFeatures.BinaryPayloads) && nodes.Any(node => node.Codec != 0 || node.Command != 0 && registration.TryResolveId(SidecarRegistrationKind.Command, node.Command)!.ResultCodecId != 0))
                throw new SidecarProtocolException("UI action requires unnegotiated binary payloads.");
            if (nodes.Sum(node => node.Image is null ? 0L : (long)node.Image.Width * node.Image.Height * 4) > 8 * 1024 * 1024
                || nodes.Select(node => node.Image).OfType<PngImage>().DistinctBy(image => image.Key).Sum(image => (long)image.Bytes.Length) > 8 * 1024 * 1024)
                throw new SidecarProtocolException("UI image decoded-pixel budget exceeded.");
            modules.Add(new(Runtime.Resolve(slot), card, nodes));
            if (modules.Count > 32) throw new SidecarProtocolException("Session UI module budget exceeded.");
        }
        return modules.ToArray();
    }
    private static XsrPreparedUiNode PrepareNode(SidecarUiNode node, SidecarRegistrationSet registration,
        SidecarStateMirror mirror, SidecarHostCache cache, HashSet<string> resources, Dictionary<string, PngImage> images)
    {
        SidecarRegistrationEntry? command = null;
        if (node.Command.Length != 0)
        {
            command = registration.TryResolve(SidecarRegistrationKind.Command, XsrSemanticId.Parse(node.Command))
                ?? throw new SidecarProtocolException("UI action refers to an undeclared session command.");
            uint expected = node.Kind == SidecarUiNodeKind.Toggle ? SidecarWireCodecs.Bool : SidecarWireCodecs.Utf8String;
            if (command.CodecId != expected) throw new SidecarProtocolException("UI action argument codec mismatch.");
        }
        XsrUiStateBinding? Resolve(string semantic, bool boolean)
        {
            if (semantic.Length == 0) return null;
            var entry = registration.TryResolve(SidecarRegistrationKind.State, XsrSemanticId.Parse(semantic))
                ?? throw new SidecarProtocolException("UI binding refers to an undeclared session state.");
            if (boolean ? entry.CodecId != SidecarWireCodecs.Bool : entry.CodecId > SidecarWireCodecs.F64)
                throw new SidecarProtocolException("UI binding value codec mismatch.");
            if (node.Kind == SidecarUiNodeKind.TextInput && semantic == node.ValueState && entry.CodecId != SidecarWireCodecs.Utf8String)
                throw new SidecarProtocolException("UI input binding must be string.");
            return new(mirror.TryResolve(entry.SemanticId) ?? throw new SidecarProtocolException("UI binding is not in the mirror."), entry.CodecId);
        }
        PngImage? image = null;
        if (node.Kind == SidecarUiNodeKind.Image)
        {
            if (!resources.Contains(node.Resource)) throw new SidecarProtocolException("UI image is outside the verified resource grant.");
            if (!images.TryGetValue(node.Resource, out image))
            {
                if (!cache.TryGetResource(XsrSemanticId.Parse(node.Resource), out var bytes)
                    || bytes is null || (image = PngImage.TryCreateResourceIcon(bytes)) is null)
                    throw new SidecarProtocolException("UI image is outside the verified, bounded resource grant.");
                images.Add(node.Resource, image);
            }
        }
        return new(node, command?.ContractId ?? 0, command?.CodecId ?? 0, Resolve(node.ValueState, node.Kind == SidecarUiNodeKind.Toggle),
            Resolve(node.EnabledState, true), Resolve(node.VisibleState, true), image);
    }

}

public sealed record XsrUiNodeView(ushort Id, ushort Parent, SidecarUiNodeKind Kind, string Label, string Value,
    bool ToggleValue, bool Enabled, bool Visible, PngImage? Image);
public sealed record XsrUiModuleView(Guid Activation, SidecarUiCard Card, IReadOnlyList<XsrUiNodeView> Nodes);
internal sealed record XsrUiStateBinding(XsrStateId Id, uint Codec);
internal sealed record XsrPreparedUiNode(SidecarUiNode Node, uint Command, uint Codec, XsrUiStateBinding? Value,
    XsrUiStateBinding? Enabled, XsrUiStateBinding? Visible, PngImage? Image);
internal sealed record XsrPreparedModule(int Index, SidecarUiCard Card, XsrPreparedUiNode[] Nodes);

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

    internal ModuleLease Activate(XsrPreparedModule[] prepared, SidecarHostSession session)
    {
        var lease = new ModuleLease(this, session);
        List<(ModuleLease Lease, int Slot)> superseded = [];
        lock (_gate)
        {
            var table = _table.Select(items => items.ToList()).ToArray();
            foreach (var module in prepared)
            {
                if (table[module.Index].LastOrDefault() is { } prior && prior.Lease != lease) superseded.Add((prior.Lease, module.Index));
                table[module.Index].Add(new(lease, module));
            }
            if (table.Sum(items => items.Count) > 64 || table.Any(items => items.Count > 8))
                throw new SidecarProtocolException("Active UI module budget exceeded.");
            _table = table.Select(items => items.ToArray()).ToArray();
            lease.Publishes = QueueSnapshot();
        }
        foreach (var prior in superseded) prior.Lease.CancelSlot(prior.Slot);
        lease.Attach();
        return lease;
    }

    private void Retire(ModuleLease lease)
    {
        bool publish;
        lock (_gate)
        {
            var previous = _table.Select(items => items.LastOrDefault()).ToArray();
            _table = _table.Select(items => items.Where(item => item.Lease != lease).ToArray()).ToArray();
            for (int i = 0; i < _table.Length; i++)
                if (_table[i].LastOrDefault() is { } restored && restored != previous[i]) restored.Identity = Guid.NewGuid();
            publish = QueueSnapshot();
        }
        if (publish) PublishPending();
    }

    private bool QueueSnapshot()
    {
        _pending = new(_table.Select(items => items.LastOrDefault() is { } item ? BuildView(item) : null).ToArray());
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

    private void Refresh(ModuleLease lease, XsrStateChange change)
    {
        bool publish;
        lock (_gate)
        {
            if (lease.Retired || !_table.Any(items => items.LastOrDefault() is { } active && active.Lease == lease
                && active.Module.Nodes.Any(node => node.Value?.Id == change.Id || node.Enabled?.Id == change.Id || node.Visible?.Id == change.Id))) return;
            publish = QueueSnapshot();
        }
        if (publish) PublishPending();
    }

    private static XsrUiModuleView BuildView(ActiveModule active)
    {
        var mirror = active.Lease.Session.Mirror?.Store;
        bool Boolean(XsrUiStateBinding? binding, bool fallback)
        {
            if (binding is null) return fallback;
            if (mirror is null) return false;
            var read = mirror.Read<bool>(binding.Id); return read.IsAvailable && read.HasValue && read.Value;
        }
        string Value(XsrUiStateBinding? binding)
        {
            if (binding is null || mirror is null) return string.Empty;
            string text = binding.Codec switch
            {
                SidecarWireCodecs.Utf8String => ReadText(mirror.Read<string>(binding.Id)),
                SidecarWireCodecs.Bool => ReadText(mirror.Read<bool>(binding.Id)),
                SidecarWireCodecs.I32 => ReadText(mirror.Read<int>(binding.Id)),
                SidecarWireCodecs.I64 => ReadText(mirror.Read<long>(binding.Id)),
                SidecarWireCodecs.F64 => ReadText(mirror.Read<double>(binding.Id)),
                _ => string.Empty
            };
            return string.Concat(text.Take(512).Where(c => !char.IsControl(c)));
        }
        var views = new XsrUiNodeView[active.Module.Nodes.Length];
        for (int i = 0; i < views.Length; i++)
        {
            var prepared = active.Module.Nodes[i]; var node = prepared.Node;
            bool enabled = !active.Lease.Retired && Boolean(prepared.Enabled, true)
                && (prepared.Value is null || mirror is not null && Available(mirror, prepared.Value));
            bool visible = Boolean(prepared.Visible, true);
            if (node.Parent != 0) { enabled &= views[node.Parent - 1].Enabled; visible &= views[node.Parent - 1].Visible; }
            views[i] = new(node.Id, node.Parent, node.Kind, node.Label, Value(prepared.Value),
                Boolean(node.Kind == SidecarUiNodeKind.Toggle ? prepared.Value : null, false), enabled, visible, prepared.Image);
        }
        return new(active.Identity, active.Module.Card, Array.AsReadOnly(views));
    }
    private static string ReadText<T>(XsrStateValue<T> read) => read.IsAvailable && read.HasValue
        ? Convert.ToString(read.Value, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
    private static bool Available(XsrStateStore store, XsrUiStateBinding binding) => binding.Codec switch
    {
        SidecarWireCodecs.Utf8String => store.Read<string>(binding.Id).IsAvailable,
        SidecarWireCodecs.Bool => store.Read<bool>(binding.Id).IsAvailable,
        SidecarWireCodecs.I32 => store.Read<int>(binding.Id).IsAvailable,
        SidecarWireCodecs.I64 => store.Read<long>(binding.Id).IsAvailable,
        SidecarWireCodecs.F64 => store.Read<double>(binding.Id).IsAvailable,
        _ => false
    };

    /// <summary>Re-admits a captured node against the currently winning live activation.</summary>
    public async ValueTask<XsrResult> DispatchAsync(int slot, Guid activation, ushort nodeId, string? draft = null,
        CancellationToken cancellationToken = default)
    {
        ActiveModule? active; XsrPreparedUiNode? prepared; CancellationToken activationToken;
        lock (_gate)
        {
            active = slot >= 0 && slot < _table.Length ? _table[slot].LastOrDefault() : null;
            prepared = active?.Module.Nodes.FirstOrDefault(item => item.Node.Id == nodeId);
            if (active is null || active.Identity != activation || active.Lease.Retired || prepared is null || prepared.Command == 0)
                return XsrResult.Failure(new XsrError(XsrErrorKind.Unavailable, XsrSemanticId.Parse("xsr.unavailable"), "The plugin view is retired or unavailable."));
            var view = BuildView(active);
            var node = view.Nodes[nodeId - 1];
            bool enabled = node.Enabled && node.Visible;
            for (ushort parent = node.Parent; enabled && parent != 0; parent = view.Nodes[parent - 1].Parent)
                enabled = view.Nodes[parent - 1].Enabled && view.Nodes[parent - 1].Visible;
            if (!enabled) return XsrResult.Failure(new XsrError(XsrErrorKind.Unavailable, XsrSemanticId.Parse("xsr.unavailable"), "The plugin view is retired or unavailable."));
            activationToken = active.Lease.TokenFor(slot);
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, activationToken);
        if (stop.IsCancellationRequested) return XsrResult.Failure(XsrRuntimeErrors.Cancelled());
        if (prepared.Node.Kind == SidecarUiNodeKind.Toggle)
        {
            var current = BuildView(active).Nodes[nodeId - 1].ToggleValue;
            var result = await active.Lease.Session.SendBinaryCommandByIdAsync(prepared.Command,
                new(SidecarWireCodecs.Bool, [current ? (byte)0 : (byte)1]), TimeSpan.FromSeconds(10), stop.Token).ConfigureAwait(false);
            return result.IsSuccess ? XsrResult.Success() : XsrResult.Failure(result.Error!);
        }
        string argument = prepared.Node.Kind == SidecarUiNodeKind.TextInput ? draft ?? "" : prepared.Node.Argument;
        if (argument.Length > 512 || argument.Any(char.IsControl)) return XsrResult.Failure(XsrRuntimeErrors.ContractMismatch());
        // String commands may return a generated binary result; the action acknowledges it
        // through the binary API when negotiated instead of assuming a string result codec.
        if (active.Lease.Session.NegotiatedFeatures.HasFlag(SidecarFeatures.BinaryPayloads))
        {
            var result = await active.Lease.Session.SendBinaryCommandByIdAsync(prepared.Command,
                new(SidecarWireCodecs.Utf8String, System.Text.Encoding.UTF8.GetBytes(argument)), TimeSpan.FromSeconds(10), stop.Token).ConfigureAwait(false);
            return result.IsSuccess ? XsrResult.Success() : XsrResult.Failure(result.Error!);
        }
        return await active.Lease.Session.SendCommandByIdAsync(prepared.Command, argument, TimeSpan.FromSeconds(10), stop.Token).ConfigureAwait(false);
    }

    private sealed record ActiveModule(ModuleLease Lease, XsrPreparedModule Module)
    { internal Guid Identity { get; set; } = Guid.NewGuid(); }
    internal sealed class ModuleLease(XsrUiModuleRuntime owner, SidecarHostSession session) : IDisposable
    {
        private int _retired;
        private readonly object _sourceGate = new();
        private readonly Dictionary<int, CancellationTokenSource> _sources = [];
        internal SidecarHostSession Session { get; } = session;
        internal CancellationToken TokenFor(int slot)
        {
            lock (_sourceGate)
            {
                if (!_sources.TryGetValue(slot, out var source) || source.IsCancellationRequested)
                { source = new(); _sources[slot] = source; }
                if (Retired) source.Cancel();
                return source.Token;
            }
        }
        internal void CancelSlot(int slot)
        {
            CancellationTokenSource? source; lock (_sourceGate) _sources.TryGetValue(slot, out source);
            source?.Cancel();
        }
        internal bool Retired => Volatile.Read(ref _retired) != 0;
        internal void Attach() { if (Session.Mirror is { } mirror) mirror.Store.Changed += OnChanged; }
        private void OnChanged(XsrStateChange change) { if (!Retired) owner.Refresh(this, change); }
        internal bool Publishes { get; set; }
        internal void Publish() { if (Publishes) owner.PublishPending(); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _retired, 1) != 0) return;
            if (Session.Mirror is { } mirror) mirror.Store.Changed -= OnChanged;
            CancellationTokenSource[] sources; lock (_sourceGate) sources = _sources.Values.ToArray();
            foreach (var source in sources) source.Cancel();
            owner.Retire(this);
        }
    }
}
