using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Nexa.Services.Network;

/// <summary>Bounded HTTPS JSON DNS queries, with system bootstrap and system-DNS fallback.</summary>
internal sealed class DnsOverHttpsResolver : IDisposable
{
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly HttpClient _client;
    private readonly string[] _endpoints;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _system;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    internal DnsOverHttpsResolver(bool mainlandChina, int proxyMode)
        : this(new HttpClient(CreateBootstrapHandler(proxyMode))
        { Timeout = TimeSpan.FromSeconds(8) }, mainlandChina, Dns.GetHostAddressesAsync)
    { }

    internal static SocketsHttpHandler CreateBootstrapHandler(int proxyMode)
    {
        if (proxyMode is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(proxyMode));
        return new() { UseProxy = proxyMode == 1, AllowAutoRedirect = false, UseCookies = false };
    }

    internal DnsOverHttpsResolver(HttpClient client, bool mainlandChina,
        Func<string, CancellationToken, Task<IPAddress[]>> system, TimeProvider? clock = null)
    {
        _client = client; _system = system; _clock = clock ?? TimeProvider.System;
        _endpoints = mainlandChina ? ["https://cloudflare-dns.com/dns-query", "https://doh.pub/dns-query"]
            : ["https://cloudflare-dns.com/dns-query"];
    }

    internal async Task<IPAddress[]> ResolveAsync(string host, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (IPAddress.TryParse(host, out IPAddress? literal)) return [literal];
        if (_cache.TryGetValue(host, out CacheEntry? entry) && entry.Expires > _clock.GetUtcNow()) return entry.Addresses.ToArray();
        foreach (string endpoint in _endpoints)
        {
            var results = await Task.WhenAll(QueryAsync(endpoint, host, 1, token), QueryAsync(endpoint, host, 28, token)).ConfigureAwait(false);
            IPAddress[] addresses = results.SelectMany(result => result.Addresses).Distinct()
                .OrderBy(address => address.AddressFamily == AddressFamily.InterNetworkV6 ? 0 : 1).ToArray();
            if (addresses.Length == 0) continue;
            int ttl = results.Where(result => result.Addresses.Length != 0).Min(result => result.Ttl);
            if (ttl > 0)
            {
                // Prevent unbounded host growth while retaining active DNS entries.
                if (_cache.Count >= 1024) _cache.Clear();
                _cache[host] = new(addresses.ToArray(), _clock.GetUtcNow().AddSeconds(Math.Min(ttl, 300)));
            }
            return addresses;
        }
        return await _system(host, token).ConfigureAwait(false);
    }

    private async Task<QueryResult> QueryAsync(string endpoint, string host, int type, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "?name=" + Uri.EscapeDataString(host) + "&type=" + type);
            request.Headers.Accept.ParseAdd("application/dns-json");
            using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) return new([], 0);
            await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using MemoryStream bounded = new();
            byte[] buffer = new byte[4096];
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0) break;
                if (bounded.Length + read > MaximumResponseBytes) return new([], 0);
                bounded.Write(buffer, 0, read);
            }
            using JsonDocument document = JsonDocument.Parse(bounded.GetBuffer().AsMemory(0, (int)bounded.Length), new() { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Status", out var status)
                || status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out int code) || code != 0
                || !root.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array) return new([], 0);
            List<IPAddress> addresses = []; int ttl = 300;
            foreach (JsonElement answer in answers.EnumerateArray())
            {
                if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("type", out var kind)
                    || kind.ValueKind != JsonValueKind.Number || !kind.TryGetInt32(out int value) || value != type
                    || !answer.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String
                    || !IPAddress.TryParse(data.GetString(), out IPAddress? address)
                    || address.AddressFamily != (type == 1 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6)) continue;
                addresses.Add(address);
                ttl = Math.Min(ttl, answer.TryGetProperty("TTL", out var expiry) && expiry.ValueKind == JsonValueKind.Number
                    && expiry.TryGetInt32(out int seconds) ? Math.Max(0, seconds) : 0);
            }
            return new(addresses.ToArray(), ttl);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or JsonException
            || failure is OperationCanceledException && !token.IsCancellationRequested)
        { return new([], 0); }
    }

    public void Dispose() => _client.Dispose();
    private sealed record CacheEntry(IPAddress[] Addresses, DateTimeOffset Expires);
    private sealed record QueryResult(IPAddress[] Addresses, int Ttl);
}
