using Nexa.Sidecar.Protocol;
using Nexa.Xsr.State;

namespace Nexa.Xsr.Runtime;

/// <summary>
/// The host-side session lifecycle. Handshaking, registering (declarations accepted), ready
/// (state snapshot committed), active (runtime behavior), closed, failed. Deactivation returns
/// from active to ready without re-registration.
/// </summary>
public enum SidecarSessionState
{
    Handshaking = 1,
    Registering = 2,
    Ready = 3,
    Active = 4,
    Closed = 5,
    Failed = 6,
}

/// <summary>
/// One accepted registration declaration, carrying its session-local contract ID. Contract IDs
/// are per-kind ordinals starting at 1 in declaration order; both sides derive the identical
/// table from the registration stream, so the data plane never carries semantic strings.
/// </summary>
public sealed record SidecarRegistrationEntry(
    SidecarRegistrationKind Kind,
    XsrSemanticId SemanticId,
    uint ContractId,
    uint Flags,
    uint CodecId)
{
    /// <summary>Gets the declared result or stream-chunk codec; zero preserves the legacy string contract.</summary>
    public uint ResultCodecId { get; init; }
}

/// <summary>
/// The accepted registration of one session with the session-local contract table.
/// </summary>
public sealed class SidecarRegistrationSet
{
    private const int MaximumEntries = 4096;
    private const uint MaximumKind = 13;
    private readonly Dictionary<(SidecarRegistrationKind Kind, XsrSemanticId Semantic), SidecarRegistrationEntry> _byContract;
    private readonly SidecarRegistrationEntry?[][] _byNumericId;

    public SidecarRegistrationSet(IReadOnlyList<SidecarRegistrationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > MaximumEntries)
            throw new SidecarProtocolException("The registration table exceeds its item budget.");

        SidecarRegistrationEntry[] owned = new SidecarRegistrationEntry[entries.Count];
        int[] counts = new int[MaximumKind + 1];
        HashSet<XsrSemanticId> semantics = [];
        for (int index = 0; index < owned.Length; index++)
        {
            SidecarRegistrationEntry entry = entries[index]
                ?? throw new SidecarProtocolException("A registration table entry is missing.");
            uint kind = (uint)entry.Kind;
            if (kind is 0 or > MaximumKind || !entry.SemanticId.IsAssigned
                || entry.SemanticId.Value.Length > 256 || !semantics.Add(entry.SemanticId))
                throw new SidecarProtocolException("A registration kind or semantic identity is invalid or duplicated.");
            if (entry.CodecId > SidecarValueCodecs.GeneratedDto || entry.ResultCodecId > SidecarValueCodecs.GeneratedDto)
                throw new SidecarProtocolException("A registration codec is unknown.");
            if (entry.Kind is not (SidecarRegistrationKind.Command or SidecarRegistrationKind.Query
                or SidecarRegistrationKind.State or SidecarRegistrationKind.Event or SidecarRegistrationKind.Stream)
                && entry.CodecId != 0)
                throw new SidecarProtocolException("This registration kind cannot declare a value codec.");
            if (entry.Kind is not (SidecarRegistrationKind.Command or SidecarRegistrationKind.Query or SidecarRegistrationKind.Stream)
                && entry.ResultCodecId != 0)
                throw new SidecarProtocolException("This registration kind cannot declare a result codec.");
            owned[index] = entry;
            counts[kind]++;
        }

        _byNumericId = new SidecarRegistrationEntry?[MaximumKind + 1][];
        for (int kind = 0; kind < counts.Length; kind++)
            _byNumericId[kind] = new SidecarRegistrationEntry?[counts[kind] + 1];

        Entries = Array.AsReadOnly(owned);
        _byContract = [];
        foreach (SidecarRegistrationEntry entry in owned)
        {
            SidecarRegistrationEntry?[] table = _byNumericId[(uint)entry.Kind];
            if (entry.ContractId == 0 || entry.ContractId >= table.Length || table[entry.ContractId] is not null)
                throw new SidecarProtocolException("Registration numeric IDs must be unique contiguous per-kind ordinals starting at one.");
            table[entry.ContractId] = entry;
            _byContract.Add((entry.Kind, entry.SemanticId), entry);
        }
    }

    public IReadOnlyList<SidecarRegistrationEntry> Entries { get; }

    public IEnumerable<XsrSemanticId> Commands => OfKind(SidecarRegistrationKind.Command);

    public IEnumerable<XsrSemanticId> Queries => OfKind(SidecarRegistrationKind.Query);

    public IEnumerable<XsrSemanticId> States => OfKind(SidecarRegistrationKind.State);

    public IEnumerable<XsrSemanticId> Events => OfKind(SidecarRegistrationKind.Event);

    public IEnumerable<XsrSemanticId> UiModules => OfKind(SidecarRegistrationKind.UiModule);

    public IEnumerable<XsrSemanticId> Resources => OfKind(SidecarRegistrationKind.Resource);

    public IEnumerable<XsrSemanticId> Streams => OfKind(SidecarRegistrationKind.Stream);

    /// <summary>
    /// Resolves one declared contract to its session-local entry, or null when the semantic was
    /// not registered under that kind — the capability boundary for the data plane.
    /// </summary>
    public SidecarRegistrationEntry? TryResolve(SidecarRegistrationKind kind, XsrSemanticId semantic) =>
        _byContract.TryGetValue((kind, semantic), out SidecarRegistrationEntry? entry) ? entry : null;

    /// <summary>Resolves a session-local numeric contract without scanning or allocating.</summary>
    public SidecarRegistrationEntry? TryResolveId(SidecarRegistrationKind kind, uint contractId)
    {
        uint numericKind = (uint)kind;
        if (numericKind is 0 or > MaximumKind) return null;
        SidecarRegistrationEntry?[] table = _byNumericId[numericKind];
        return contractId > 0 && contractId < table.Length ? table[contractId] : null;
    }

    private IEnumerable<XsrSemanticId> OfKind(SidecarRegistrationKind kind) =>
        Entries.Where(entry => entry.Kind == kind).Select(entry => entry.SemanticId);
}

/// <summary>
/// The per-session state mirror: one revisioned store whose cells correspond to the states the
/// sidecar registered, typed by the declared codec (codec 0 = UTF-8 string). Cells start
/// unavailable; the pre-activation state snapshot fills them, and the mirror is coherent only
/// after that snapshot commits — before READY, before activation. The store is the renderer's
/// only view of sidecar state.
/// </summary>
public sealed class SidecarStateMirror
{
    public SidecarStateMirror(string pluginName, XsrStateStore store)
    {
        PluginName = pluginName;
        Store = store;
    }

    public string PluginName { get; }

    public XsrStateStore Store { get; }

    /// <summary>Publishes a privately completed initial snapshot while preserving the accepted mirror identity.</summary>
    internal void AdoptSnapshot(SidecarStateMirror candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!StringComparer.Ordinal.Equals(PluginName, candidate.PluginName))
            throw new SidecarProtocolException("The initial snapshot belongs to another plugin.");
        Store.CommitInitialSnapshot(candidate.Store);
    }

    public XsrStateId? TryResolve(XsrSemanticId semantic) =>
        Store.TryResolve(semantic, out XsrStateId stateId) ? stateId : null;

    /// <summary>
    /// Creates the mirror for one session's declared states, with each cell typed by its
    /// declared codec.
    /// </summary>
    public static SidecarStateMirror Create(
        string pluginName,
        IReadOnlyList<SidecarRegistrationEntry> stateEntries)
    {
        XsrStateStoreBuilder builder = new();
        foreach (SidecarRegistrationEntry entry in stateEntries)
        {
            switch (entry.CodecId)
            {
                case SidecarValueCodecs.Utf8String:
                    builder.Cell<string>(entry.SemanticId, pluginName);
                    break;
                case SidecarValueCodecs.Bool:
                    builder.Cell<bool>(entry.SemanticId, pluginName);
                    break;
                case SidecarValueCodecs.I32:
                    builder.Cell<int>(entry.SemanticId, pluginName);
                    break;
                case SidecarValueCodecs.I64:
                    builder.Cell<long>(entry.SemanticId, pluginName);
                    break;
                case SidecarValueCodecs.F64:
                    builder.Cell<double>(entry.SemanticId, pluginName);
                    break;
                case SidecarValueCodecs.Bytes:
                case SidecarValueCodecs.GeneratedDto:
                    builder.Cell<byte[]>(entry.SemanticId, pluginName);
                    break;
                default:
                    throw new SidecarProtocolException(
                        $"The state '{entry.SemanticId}' declares unknown codec {entry.CodecId}.");
            }
        }

        return new SidecarStateMirror(pluginName, builder.Build());
    }

    /// <summary>
    /// Publishes one raw codec-encoded wire value into the declared typed cell.
    /// </summary>
    public long PublishFromWire(SidecarRegistrationEntry entry, byte[] encodedValue)
    {
        XsrStateId stateId = TryResolve(entry.SemanticId)
            ?? throw new SidecarProtocolException(
                $"The state '{entry.SemanticId}' is not part of this mirror.");
        object value = SidecarValueCodecs.Decode(entry.CodecId, encodedValue);
        return entry.CodecId switch
        {
            SidecarValueCodecs.Utf8String => Store.Publish(stateId, (string)value),
            SidecarValueCodecs.Bool => Store.Publish(stateId, (bool)value),
            SidecarValueCodecs.I32 => Store.Publish(stateId, (int)value),
            SidecarValueCodecs.I64 => Store.Publish(stateId, (long)value),
            SidecarValueCodecs.F64 => Store.Publish(stateId, (double)value),
            SidecarValueCodecs.Bytes or SidecarValueCodecs.GeneratedDto => Store.Publish(stateId, (byte[])value),
            _ => throw new SidecarProtocolException(
                $"The state '{entry.SemanticId}' declares unknown codec {entry.CodecId}."),
        };
    }
}

/// <summary>
/// Reports session lifecycle transitions and failures. Observer failures never change the
/// session.
/// </summary>
public interface ISidecarSessionObserver
{
    void OnStateChanged(SidecarSessionState state);
}
