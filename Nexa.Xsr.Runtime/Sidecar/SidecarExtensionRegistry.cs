using System.Security.Cryptography;
using Nexa.Sidecar.Protocol;

namespace Nexa.Xsr.Runtime;

public sealed record SidecarExtensionRegistration(SidecarRegistrationKind Kind, XsrSemanticId SemanticId,
    XsrSemanticId Target, uint ContractId, uint Flags, ReadOnlyMemory<byte> Payload);

/// <summary>Session-owned binary declarations. No reflection or executable CLR payloads.</summary>
public sealed class SidecarExtensionRegistry
{
    private readonly List<SidecarExtensionRegistration> _entries = [];
    public IReadOnlyList<SidecarExtensionRegistration> Entries => _entries.AsReadOnly();

    internal void Add(SidecarRegistrationEntry entry, SidecarRegistrationItem item)
    {
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(item.Payload!), item.ContentHash!))
            throw new SidecarProtocolException("Extension payload hash mismatch.");
        _entries.Add(new(entry.Kind, entry.SemanticId, XsrSemanticId.Parse(item.TargetSemanticId!),
            entry.ContractId, entry.Flags, item.Payload!.ToArray()));
    }
}
