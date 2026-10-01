using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Nexa.Services.Minecraft.Install;

internal interface IVerifiedAssetIndexSource
{
    bool HasVerifiedIndexBytes { get; }
    Task<byte[]> FetchVerifiedAssetIndexAsync(string url, string? sha1, long size, CancellationToken token);
}

/// <summary>Origin authority and raw-byte integrity are checked before metadata is parsed.</summary>
internal static class AuthoritativeMetadata
{
    internal static void RequireMojangOrigin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.Host is not ("piston-meta.mojang.com" or "launchermeta.mojang.com" or "launcher.mojang.com"))
            throw new InvalidDataException("Minecraft 元数据必须来自官方 HTTPS 来源。");
    }

    internal static void RequireSameOrigin(Uri requested, Uri actual)
    {
        if (actual.Scheme != Uri.UriSchemeHttps || actual.UserInfo.Length != 0
            || actual.Port != requested.Port || !actual.Host.Equals(requested.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("权威元数据请求被重定向到其他来源。");
    }

    internal static async Task<byte[]> ReadAsync(HttpClient http, string url, int maximumBytes, CancellationToken token)
    {
        var origin = new Uri(url);
        if (origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0)
            throw new InvalidDataException("权威元数据必须使用 HTTPS。");
        using var request = new HttpRequestMessage(HttpMethod.Get, origin);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        RequireSameOrigin(origin, response.RequestMessage?.RequestUri ?? origin);
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("元数据响应过大。");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new InvalidDataException("元数据响应过大。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    internal static JsonObject ParseVerified(byte[] bytes, string? sha1, long size)
    {
        if (size >= 0 && bytes.LongLength != size) throw new InvalidDataException("元数据长度校验失败。");
        if (sha1 is not null)
        {
            if (sha1.Length != 40 || !sha1.All(char.IsAsciiHexDigit)) throw new InvalidDataException("元数据 SHA-1 无效。");
            // Mojang's published metadata identity uses SHA-1; it is not a signature algorithm.
#pragma warning disable CA5350
            string actual = Convert.ToHexString(SHA1.HashData(bytes));
#pragma warning restore CA5350
            if (!actual.Equals(sha1, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("元数据 SHA-1 校验失败。");
        }
        return JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("元数据不是 JSON 对象。");
    }
}
