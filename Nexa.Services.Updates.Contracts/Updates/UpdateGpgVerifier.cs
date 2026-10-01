



namespace Nexa.Services.Updates;

/// <summary>
/// Verifies update artifacts against the release signing key. Implementations must throw
/// <see cref="InvalidDataException"/> for any verification failure so the update flow can
/// treat every outcome uniformly as "untrusted".
/// </summary>
public interface IUpdateSignatureVerifier
{
    Task VerifyAsync(Stream content, Stream detachedSignature, CancellationToken cancellationToken = default);
}
