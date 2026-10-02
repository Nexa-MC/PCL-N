using System.Text;
using System.Threading.Channels;
using Nexa.Sidecar.Protocol;

namespace Nexa.Xsr.Runtime;

public enum XsrSignalKind { Event, Intent }

public sealed record XsrSignalDefinition(XsrSemanticId Target, XsrSignalKind Kind, bool AllowsCatch = false);

/// <summary>Bound once at composition; emitting uses an instance-owned numeric index.</summary>
public sealed class XsrSignalPoint
{
    internal XsrSignalPoint(XsrSignalRuntime owner, int index, XsrSignalDefinition definition)
    { Owner = owner; Index = index; Definition = definition; }
    internal XsrSignalRuntime Owner { get; }
    internal int Index { get; }
    internal XsrSignalDefinition Definition { get; }
}

public sealed class XsrSignalAdmission
{
    private readonly HashSet<XsrSemanticId> _allowed;
    internal XsrSignalRuntime Runtime { get; }
    public XsrSignalAdmission(XsrSignalRuntime runtime, params XsrSemanticId[] targets)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(targets);
        _allowed = new(targets);
        foreach (var target in targets) _ = runtime.Resolve(target);
    }

    internal XsrPreparedSignal[] Prepare(IReadOnlyList<SidecarExtensionRegistration> entries)
    {
        List<XsrPreparedSignal> prepared = [];
        foreach (var entry in entries)
        {
            if (!IsSignal(entry.Kind)) continue;
            if (!_allowed.Contains(entry.Target) || entry.Flags != 0)
                throw new SidecarProtocolException("Signal target or flags are not granted by the host.");
            var point = Runtime.Resolve(entry.Target);
            bool intent = entry.Kind is SidecarRegistrationKind.IntentCatch or SidecarRegistrationKind.IntentWait;
            if (intent != (point.Definition.Kind == XsrSignalKind.Intent))
                throw new SidecarProtocolException("Signal registration kind does not match its target.");
            var payload = entry.Payload.Span;
            if (payload.Length is < 5 or > 517 || !payload[..4].SequenceEqual("NXS1"u8) || payload[4] > 1)
                throw new SidecarProtocolException("Invalid signal predicate ABI or budget.");
            bool consume = payload[4] == 1;
            if (consume && (!point.Definition.AllowsCatch || entry.Kind is not
                (SidecarRegistrationKind.EventCatch or SidecarRegistrationKind.IntentCatch)))
                throw new SidecarProtocolException("This signal target does not allow consumption.");
            string match;
            try { match = new UTF8Encoding(false, true).GetString(payload[5..]); }
            catch (DecoderFallbackException) { throw new SidecarProtocolException("Invalid signal predicate UTF-8."); }
            prepared.Add(new(point, entry.Kind, entry.ContractId, match, consume));
            if (prepared.Count > 128) throw new SidecarProtocolException("Session signal binding budget exceeded.");
        }
        return prepared.ToArray();
    }

    internal static bool IsSignal(SidecarRegistrationKind kind) => kind is SidecarRegistrationKind.EventCatch
        or SidecarRegistrationKind.EventListen or SidecarRegistrationKind.IntentCatch or SidecarRegistrationKind.IntentWait;
}

internal sealed record XsrPreparedSignal(XsrSignalPoint Point, SidecarRegistrationKind Kind,
    uint ContractId, string Match, bool Consume);

/// <summary>Local predicates and bounded asynchronous observation; never synchronous IPC.</summary>
public sealed class XsrSignalRuntime
{
    private readonly object _gate = new();
    private readonly Dictionary<XsrSemanticId, XsrSignalPoint> _points = [];
    private Binding[][] _table;

    public XsrSignalRuntime(params XsrSignalDefinition[] definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(definitions.Length, 256);
        _table = new Binding[definitions.Length][];
        for (int i = 0; i < definitions.Length; i++)
        {
            var definition = definitions[i];
            _ = XsrSemanticId.Parse(definition.Target.Value);
            if (!Enum.IsDefined(definition.Kind)) throw new ArgumentException("Unknown signal kind.", nameof(definitions));
            _points.Add(definition.Target, new(this, i, definition));
            _table[i] = [];
        }
    }

    public XsrSignalPoint Resolve(XsrSemanticId target) => _points.TryGetValue(target, out var point)
        ? point : throw new ArgumentException("Unknown host signal point.", nameof(target));

    /// <returns>True only when an authorized local catch accepted this signal.</returns>
    public bool Emit(XsrSignalPoint point, string value)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(value);
        if (point.Owner != this) throw new ArgumentException("Point belongs to another runtime.", nameof(point));
        if (value.Length > 1024) return false;
        bool consumed = false;
        foreach (var binding in Volatile.Read(ref _table)[point.Index])
        {
            if (binding.Prepared.Match.Length != 0 && !StringComparer.Ordinal.Equals(binding.Prepared.Match, value)) continue;
            if (binding.Lease.Publish(binding, value)) consumed |= binding.Prepared.Consume;
        }
        return consumed;
    }

    internal SignalLease Activate(XsrPreparedSignal[] prepared)
    {
        lock (_gate)
        {
            var lease = new SignalLease(this);
            var next = _table.Select(bindings => bindings.ToList()).ToArray();
            foreach (var item in prepared) next[item.Point.Index].Add(new(item, lease));
            if (next.Sum(bindings => bindings.Count) > 256 || next.Any(bindings => bindings.Count > 32))
            { lease.Dispose(); throw new SidecarProtocolException("Active signal binding budget exceeded."); }
            Volatile.Write(ref _table, next.Select(bindings => bindings.ToArray()).ToArray());
            return lease;
        }
    }

    private void Retire(SignalLease lease)
    {
        lock (_gate)
            Volatile.Write(ref _table, _table.Select(bindings => bindings.Where(binding => binding.Lease != lease).ToArray()).ToArray());
    }

    internal sealed class Binding(XsrPreparedSignal prepared, SignalLease lease)
    {
        internal XsrPreparedSignal Prepared { get; } = prepared;
        internal SignalLease Lease { get; } = lease;
        internal bool Delivered { get; set; }
    }

    internal sealed class SignalLease(XsrSignalRuntime owner) : IDisposable
    {
        private readonly object _gate = new();
        private readonly Channel<SidecarHookSignal> _queue = Channel.CreateBounded<SidecarHookSignal>(new BoundedChannelOptions(128)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        private readonly CancellationTokenSource _ended = new();
        private bool _retired, _overflow;
        private ulong _sequence;
        internal Guid ActivationId { get; } = Guid.NewGuid();
        internal bool Publish(Binding binding, string value)
        {
            lock (_gate)
            {
                if (_retired || _overflow || (binding.Prepared.Kind == SidecarRegistrationKind.IntentWait && binding.Delivered)) return false;
                if (!_queue.Writer.TryWrite(new(ActivationId, binding.Prepared.Kind, binding.Prepared.ContractId, ++_sequence, value)))
                { _overflow = true; _queue.Writer.TryComplete(); return false; }
                binding.Delivered = true;
                return true;
            }
        }

        internal async Task PumpAsync(Func<SidecarHookSignal, CancellationToken, ValueTask> send, Action failed)
        {
            try
            {
                while (await _queue.Reader.WaitToReadAsync(_ended.Token).ConfigureAwait(false))
                {
                    while (_queue.Reader.TryRead(out var signal))
                    {
                        lock (_gate)
                        {
                            if (_retired) return;
                            if (_overflow) throw new SidecarProtocolException("Signal notification queue overflow.");
                        }
                        await send(signal, _ended.Token).ConfigureAwait(false);
                    }
                }
                lock (_gate) { if (_overflow && !_retired) throw new SidecarProtocolException("Signal notification queue overflow."); }
            }
            catch (OperationCanceledException) when (_ended.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or InvalidOperationException or SidecarProtocolException)
            { failed(); }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_retired) return;
                _retired = true;
                _queue.Writer.TryComplete();
            }
            _ended.Cancel();
            owner.Retire(this);
        }
    }
}
