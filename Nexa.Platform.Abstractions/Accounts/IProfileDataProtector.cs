

namespace Nexa.Services.Accounts;


public interface IProfileDataProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>Initializes OS key storage without blocking a caller thread.</summary>
public interface IAsyncProfileDataProtector : IProfileDataProtector, IDisposable
{
    ValueTask InitializeAsync(IEnumerable<Guid> keyIds, CancellationToken cancellationToken = default);
}
