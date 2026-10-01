using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Install;

namespace Nexa.Services.Minecraft.Management;

/// <summary>Read-only, bounded, hash-based update lookup; never guesses project identity from a filename.</summary>
internal static class InstanceModUpdates
{
    // Process-owned pool; per-call cancellation must never dispose another caller's connections.
    internal static HttpClient SharedHttp { get; } = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 4
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    internal static async Task<InstanceContentSnapshot> CheckAsync(HttpClient http, InstanceContentSnapshot source,
        string directory, string game, IReadOnlyList<InstallBuildSelection> components, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        source = source with
        {
            Entries = Array.AsReadOnly(source.Entries.Select(entry =>
            entry with { UpdateAvailable = null, UpdateVersion = "" }).ToArray())
        };
        string[] loaders = components.Select(item => item.Loader switch
        {
            InstallLoader.Fabric => "fabric",
            InstallLoader.Quilt => "quilt",
            InstallLoader.Forge => "forge",
            InstallLoader.NeoForge => "neoforge",
            _ => ""
        }).Where(value => value.Length > 0).Distinct().ToArray();
        if (loaders.Length == 0 || string.IsNullOrWhiteSpace(game))
            return source with { Error = "当前加载器暂不支持在线更新检查。" };
        var entries = source.Entries.ToArray();
        List<(int Index, string Hash)> files = [];
        long remaining = 2L * 1024 * 1024 * 1024;
        for (int i = 0; i < entries.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = entries[i];
            if (entry.Enabled is null || entry.PackageReadable != true || entry.Size is not { } size
                || size > 256L * 1024 * 1024 || size > remaining) continue;
            try
            {
                remaining -= size;
                string? hash = await ReadCurrentHashAsync(directory, entry, token).ConfigureAwait(false);
                if (hash is not null) files.Add((i, hash));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        try
        {
            bool changed = false;
            foreach (var batch in files.Chunk(100))
            {
                var request = new JsonObject
                {
                    ["hashes"] = new JsonArray(batch.Select(item => (JsonNode?)JsonValue.Create(item.Hash)).ToArray()),
                    ["algorithm"] = "sha512"
                };
                JsonObject installed = await Post(http, "version_files", request, token).ConfigureAwait(false);
                request["loaders"] = new JsonArray(loaders.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                request["game_versions"] = new JsonArray(JsonValue.Create(game));
                JsonObject latest = await Post(http, "version_files/update", request, token).ConfigureAwait(false);
                foreach (var file in batch)
                {
                    if (installed[file.Hash] is not JsonObject current || latest[file.Hash] is not JsonObject candidate
                        || Text(current, "project_id") is not { Length: > 0 } project
                        || Text(candidate, "project_id") != project
                        || candidate["game_versions"] is not JsonArray games || !games.Any(value => value?.ToString() == game)
                        || candidate["loaders"] is not JsonArray supported || !supported.Any(value => loaders.Contains(value?.ToString()))
                        || candidate["files"] is not JsonArray assets || assets.Count == 0) continue;
                    bool same = Text(current, "id") == Text(candidate, "id")
                        || assets.Any(asset => asset?["hashes"]?["sha512"]?.ToString() == file.Hash);
                    if (!DateTimeOffset.TryParse(Text(current, "date_published"), out var before)
                        || !DateTimeOffset.TryParse(Text(candidate, "date_published"), out var after)) continue;
                    bool update = !same && after > before;
                    try
                    {
                        if (await ReadCurrentHashAsync(directory, entries[file.Index], token).ConfigureAwait(false) != file.Hash)
                        { changed = true; continue; }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { changed = true; continue; }
                    entries[file.Index] = entries[file.Index] with
                    { UpdateAvailable = update, UpdateVersion = update ? Text(candidate, "version_number") : "" };
                }
            }
            return source with
            {
                Entries = Array.AsReadOnly(entries),
                Error = changed ? "部分文件在检查期间发生变化，请刷新后重新检查。" : source.Error
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException
            or InvalidOperationException or OperationCanceledException)
        { return source with { Entries = Array.AsReadOnly(entries), Error = "更新检查未完成，请稍后重试。未识别的模组保持未检测状态。" }; }
    }

    private static async Task<string?> ReadCurrentHashAsync(string directory, InstanceContentEntry entry, CancellationToken token)
    {
        if (!MinecraftVersionPaths.IsSafeReference(entry.Name) || entry.Size is not { } size) return null;
        string path = Path.Combine(directory, entry.Name);
        CheckLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (input.Length != size || File.GetLastWriteTimeUtc(path).Ticks != entry.ModifiedUtcTicks) return null;
        string hash = await HashAsync(input, size, token).ConfigureAwait(false);
        CheckLinks(path);
        return input.Length == size && File.GetLastWriteTimeUtc(path).Ticks == entry.ModifiedUtcTicks ? hash : null;
    }

    private static void CheckLinks(string path)
    {
        for (string? parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能检查链接目录中的模组。");
    }

    private static async Task<string> HashAsync(Stream input, long size, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        byte[] buffer = new byte[81920];
        long readTotal = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - readTotal + 1)), token).ConfigureAwait(false);
            if (read == 0) break;
            readTotal += read;
            if (readTotal > size) throw new IOException("模组文件已变化。");
            hash.AppendData(buffer, 0, read);
        }
        if (readTotal != size) throw new IOException("模组文件已变化。");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Text(JsonObject obj, string name) => obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static async Task<JsonObject> Post(HttpClient http, string route, JsonObject body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.modrinth.com/v2/" + route);
        request.Headers.UserAgent.ParseAdd("NexaCL/2.0.0 (https://github.com/PCL-N-Edition/PCL-N)");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > 8 * 1024 * 1024) throw new IOException("更新响应超过限制。");
            output.Write(buffer, 0, read);
        }
        return JsonNode.Parse(output.ToArray()) as JsonObject ?? throw new IOException("更新响应无效。");
    }
}
