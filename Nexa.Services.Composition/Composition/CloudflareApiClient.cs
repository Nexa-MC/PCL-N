using System.Security.Cryptography.X509Certificates;
using Nexa.Services.Network;

namespace Nexa.Services.Composition;

/// <summary>Owns the API Shield identity and its no-redirect HTTP connection pool.</summary>
public sealed class CloudflareApiClient : IDisposable
{
    private readonly X509Certificate2 _certificate;
    private readonly NetworkHttpClientPool? _pool;
    public HttpClient Client { get; }

    private CloudflareApiClient(X509Certificate2 certificate, NetworkHttpClientPool? transport)
    {
        _certificate = certificate;
        if (transport is null)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, ClientCertificateOptions = ClientCertificateOption.Manual };
            handler.ClientCertificates.Add(certificate);
            Client = new HttpClient(handler);
        }
        else
        {
            _pool = transport.CreateSpecialized(handler => handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate });
            Client = _pool.CreateClient(allowAutoRedirect: false);
        }
        Client.Timeout = TimeSpan.FromSeconds(15);
    }

    public static CloudflareApiClient? TryCreate(Func<Stream?>? embeddedCertificate = null) => TryCreate(embeddedCertificate, null);

    public static CloudflareApiClient? TryCreate(Func<Stream?>? embeddedCertificate, NetworkHttpClientPool? transport)
    {
        string? path = Environment.GetEnvironmentVariable("NEXA_API_CLIENT_CERT_PATH");
        string? password = Environment.GetEnvironmentVariable("NEXA_API_CLIENT_CERT_PASSWORD");
        // A launcher started by an existing terminal may inherit an older environment.
        // Read the explicitly configured user value without copying credentials into the checkout.
        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(path))
        {
            path = Environment.GetEnvironmentVariable("NEXA_API_CLIENT_CERT_PATH", EnvironmentVariableTarget.User);
            password ??= Environment.GetEnvironmentVariable("NEXA_API_CLIENT_CERT_PASSWORD", EnvironmentVariableTarget.User);
        }
        X509Certificate2 certificate;
        // Windows Schannel cannot use ephemeral PFX private keys (SEC_E_NO_CREDENTIALS).
        // UserKeySet without PersistKeySet lets certificate disposal clean up imported keys.
        X509KeyStorageFlags keyStorage = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
        if (!string.IsNullOrWhiteSpace(path))
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, keyStorage);
        else
        {
            using var stream = embeddedCertificate?.Invoke();
            if (stream is null) return null;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            certificate = X509CertificateLoader.LoadPkcs12(buffer.ToArray(), password, keyStorage);
        }
        if (!certificate.HasPrivateKey || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
        {
            certificate.Dispose();
            throw new InvalidOperationException("Cloudflare API 客户端证书不可用。");
        }
        return new CloudflareApiClient(certificate, transport);
    }

    public void Dispose() { Client.Dispose(); _pool?.Dispose(); _certificate.Dispose(); }
}
