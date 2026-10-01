

namespace Nexa.Services.Capabilities;

/// <summary>
/// One broker-level derivation: computes a Derived-kind capability from other capabilities'
/// collected values AFTER providers finish. Derivations are the only sanctioned way for a
/// capability to depend on facts owned by a different provider (the broker enforces
/// per-provider ownership at collection time).
/// </summary>
public interface ICapabilityDerivation
{
    string Id { get; }

    ICapability Evaluate(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp);
}
