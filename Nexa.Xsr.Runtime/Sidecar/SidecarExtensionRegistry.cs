using System.Security.Cryptography;
using Nexa.Sidecar.Protocol;

namespace Nexa.Xsr.Runtime;

public sealed record SidecarExtensionRegistration(SidecarRegistrationKind Kind, XsrSemanticId SemanticId,
    XsrSemanticId Target, uint ContractId, uint Flags, ReadOnlyMemory<byte> Payload);

/// <summary>Session-owned binary declarations. No reflection or executable CLR payloads.</summary>
public sealed class SidecarExtensionRegistry
{
    private readonly List<SidecarExtensionRegistration> _entries = [];
    public IReadOnlyList<SidecarExtensionRegistration> Entries => Array.AsReadOnly(_entries.Select(
        static entry => entry with { Payload = entry.Payload.ToArray() }).ToArray());

    internal void Add(SidecarRegistrationEntry entry, SidecarRegistrationItem item)
    {
        byte[] owned = item.Payload?.ToArray()
            ?? throw new SidecarProtocolException("Extension payload is missing.");
        byte[] hash = item.ContentHash?.ToArray()
            ?? throw new SidecarProtocolException("Extension payload hash is missing.");
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(owned), hash))
            throw new SidecarProtocolException("Extension payload hash mismatch.");
        _entries.Add(new(entry.Kind, entry.SemanticId, XsrSemanticId.Parse(item.TargetSemanticId!),
            entry.ContractId, entry.Flags, owned));
    }
}
