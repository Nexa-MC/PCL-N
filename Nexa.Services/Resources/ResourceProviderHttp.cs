using System.Text.Json;

namespace Nexa.Services.Resources;

public sealed class ResourceProviderHttp(HttpClient http, string? curseForgeKey = null)
{
    public RegionalPolicy CountryPolicy { get; init; } = RegionalPolicy.Current;
    internal const string Mirror = "https://mod.mcimirror.top";
    private readonly string? _key = curseForgeKey ?? Environment.GetEnvironmentVariable("Nexa_CURSEFORGE_API_KEY") ?? Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
    public Task<JsonDocument> ReadAsync(ResourceProvider provider, string path, bool mirrorFirst, CancellationToken token) => SendAsync(provider, path, mirrorFirst, null, token);
    public Task<JsonDocument> PostAsync(ResourceProvider provider, string path, bool mirrorFirst, string json, CancellationToken token) => SendAsync(provider, path, mirrorFirst, json, token);
    private async Task<JsonDocument> SendAsync(ResourceProvider provider, string path, bool mirrorFirst, string? json, CancellationToken token)
    {
        string official = provider == ResourceProvider.Modrinth ? "https://api.modrinth.com/v2/" : "https://api.curseforge.com/v1/";
        string mirror = Mirror + (provider == ResourceProvider.Modrinth ? "/modrinth/v2/" : "/curseforge/v1/");
        string[] candidates = provider == ResourceProvider.CurseForge && string.IsNullOrEmpty(_key) ? [mirror] : mirrorFirst ? [mirror, official] : [official, mirror];
        if (!CountryPolicy.IsMainlandChina)
        {
            if (provider == ResourceProvider.CurseForge && string.IsNullOrEmpty(_key))
                throw new IOException("此地区使用 CurseForge 官方接口，需要配置 CurseForge API Key。");
            candidates = [official];
        }
        Exception? last = null;
        foreach (string root in candidates)
        {
            try { return await GetAsync(root + path, token, root == official && provider == ResourceProvider.CurseForge ? _key : null, json).ConfigureAwait(false); }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException || error is OperationCanceledException && !token.IsCancellationRequested) { last = error; }
        }
        throw new IOException("资源站暂时无法访问。", last);
    }
    internal async Task<JsonDocument> GetAsync(string url, CancellationToken token, string? key = null, string? json = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(json is null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.UserAgent.ParseAdd("NexaCL/2.0 (https://github.com/PCL-N-Edition/PCL-N)");
        if (json is not null) request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        if (key is not null) request.Headers.Add("x-api-key", key);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream(); byte[] buffer = new byte[16384];
        const int limit = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("资源站响应过大。");
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - output.Length + 1)), timeout.Token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > limit) throw new InvalidDataException("资源站响应过大。");
            output.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(output.GetBuffer().AsMemory(0, (int)output.Length), new() { MaxDepth = 32 });
    }
    internal static string Text(JsonElement item, string key)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(key, out var value) || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) return "";
        string text = value.ToString(); return text[..Math.Min(text.Length, 2000)];
    }
    internal static long Number(JsonElement item, string key) => long.TryParse(Text(item, key), out long value) ? value : 0;
}
