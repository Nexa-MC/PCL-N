using Nexa.Services.Logging;

namespace Nexa.Services.Minecraft.Java;

/// <summary>HTTP transport for Mojang's Java runtime catalog and per-runtime manifests.</summary>
public sealed class HttpJavaRuntimeMetadataProvider : IJavaRuntimeMetadataProvider, IDisposable
{
    public const string RuntimeIndexUrl =
        "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public HttpJavaRuntimeMetadataProvider(LogService? log = null)
        : this(CreateDefaultClient(log), ownsClient: true)
    {
    }

    public HttpJavaRuntimeMetadataProvider(HttpClient client, bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
    }

    public async ValueTask<string> GetRuntimeIndexAsync(CancellationToken cancellationToken = default)
    {
        return await ReadBoundedAsync(RuntimeIndexUrl, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string> GetManifestAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestUrl);
        return await ReadBoundedAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadBoundedAsync(string url, CancellationToken token)
    {
        const int limit = 4 * 1024 * 1024;
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Java metadata exceeds the admission budget.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); byte[] block = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(block, token).ConfigureAwait(false); if (read == 0) break;
            if (buffer.Length + read > limit) throw new InvalidDataException("Java metadata exceeds the admission budget.");
            await buffer.WriteAsync(block.AsMemory(0, read), token).ConfigureAwait(false);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static HttpClient CreateDefaultClient(LogService? log)
    {
        HttpClient client = new(log is null ? new HttpClientHandler() : new DiagnosticHttpHandler(log, new HttpClientHandler())) { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NexaCL/2.0");
        return client;
    }
}
