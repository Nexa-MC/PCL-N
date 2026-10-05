using System.Text;

namespace Nexa.Services.Accounts;

/// <summary>Account-provider JSON is bounded before buffering, including chunked responses.</summary>
internal static class AccountProviderResponse
{
    private const int MaximumBytes = 1_048_576;
    internal static async Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is > MaximumBytes)
            throw new InvalidDataException("账户提供方响应超过大小限制。");
        await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using MemoryStream output = new(); byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumBytes) throw new InvalidDataException("账户提供方响应超过大小限制。");
            output.Write(buffer, 0, read);
        }
        token.ThrowIfCancellationRequested();
        return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }
}
