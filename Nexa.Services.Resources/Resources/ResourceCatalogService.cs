using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexa.Services.Resources;

/// <summary>Bounded, cancellable provider adapter. No UI or filesystem side effects.</summary>
public sealed class ResourceCatalogService(HttpClient http, ResourceProviderHttp? transport = null) : IResourceCatalogSource, IResourceFileSource, IResourceChangelogSource
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private static string Encode(string value) => Uri.EscapeDataString(value);
    private static string ArrayValue(string value) => new JsonArray(JsonValue.Create(value)).ToJsonString();

    public async Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token)
    {
        if (!Enum.IsDefined(query.Kind) || !Enum.IsDefined(query.Order) || query.Page is < 0 or > 499)
            throw new ArgumentException("资源筛选无效。");
        ValidateFilter(query.Text, 200); ValidateFilter(query.GameVersion, 40); ValidateFilter(query.Loader, 40);
        JsonArray facets = [];
        string type = query.Kind switch
        {
            ResourceKind.Mod => "mod",
            ResourceKind.Modpack => "modpack",
            ResourceKind.ResourcePack => "resourcepack",
            ResourceKind.Shader => "shader",
            _ => "mod"
        };
        facets.Add((JsonNode)new JsonArray(JsonValue.Create(query.Kind == ResourceKind.DataPack ? "all_project_types:datapack" : "project_type:" + type)));
        if (query.GameVersion.Length > 0) facets.Add((JsonNode)new JsonArray(JsonValue.Create("versions:" + query.GameVersion)));
        if (query.Loader.Length > 0 && query.Kind is ResourceKind.Mod or ResourceKind.Modpack or ResourceKind.Shader)
            facets.Add((JsonNode)new JsonArray(JsonValue.Create("categories:" + query.Loader.ToLowerInvariant())));
        string order = query.Order switch { ResourceOrder.Downloads => "downloads", ResourceOrder.Updated => "updated", _ => "relevance" };
        using var doc = await ReadAsync($"search?limit=20&offset={query.Page * 20}&index={order}&query={Encode(query.Text)}&facets={Encode(facets.ToJsonString())}", token, query.MirrorFirst).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("资源站返回了无效的搜索结果。");
        List<ResourceProject> entries = [];
        foreach (var item in hits.EnumerateArray().Take(20))
        {
            string id = Text(item, "project_id");
            if (Identifier(id)) entries.Add(Project(item, id) with { Kind = query.Kind });
        }
        return new(entries.AsReadOnly(), (int)Math.Clamp(Number(root, "total_hits"), 0, 10000), query.Page);
    }

    public async Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token)
    {
        if (!Identifier(query.ProjectId)) throw new ArgumentException("资源标识无效。");
        ValidateFilter(query.GameVersion, 40); ValidateFilter(query.Loader, 40);
        string suffix = "?";
        if (query.GameVersion.Length > 0) suffix += "game_versions=" + Encode(ArrayValue(query.GameVersion)) + "&";
        if (query.Loader.Length > 0) suffix += "loaders=" + Encode(ArrayValue(query.Loader.ToLowerInvariant()));
        // Both requests execute independently and are fully observed even if one fails.
        Task<JsonDocument> projectTask = ReadAsync("project/" + query.ProjectId, token, query.MirrorFirst);
        Task<JsonDocument> versionsTask = ReadAsync("project/" + query.ProjectId + "/version" + suffix, token, query.MirrorFirst);
        try { await Task.WhenAll(projectTask, versionsTask).ConfigureAwait(false); }
        catch
        {
            if (projectTask.IsCompletedSuccessfully) projectTask.Result.Dispose();
            if (versionsTask.IsCompletedSuccessfully) versionsTask.Result.Dispose();
            throw;
        }
        using var project = projectTask.Result;
        using var versions = versionsTask.Result;
        if (Text(project.RootElement, "id") != query.ProjectId || versions.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("资源详情标识不匹配。");
        List<ResourceVersion> rows = [];
        foreach (var item in versions.RootElement.EnumerateArray().Take(5000))
        {
            string id = Text(item, "id");
            if (!Identifier(id) || Text(item, "project_id") != query.ProjectId) continue;
            var games = Strings(item, "game_versions"); var loaders = Strings(item, "loaders");
            if (query.GameVersion.Length > 0 && !games.Contains(query.GameVersion)) continue;
            if (query.Loader.Length > 0 && !loaders.Contains(query.Loader, StringComparer.OrdinalIgnoreCase)) continue;
            rows.Add(Version(item, query.ProjectId));
        }
        string license = project.RootElement.TryGetProperty("license", out var value) ? Text(value, "name") : "";
        return new(Project(project.RootElement, query.ProjectId), license,
            Array.AsReadOnly(rows.OrderByDescending(item => item.Published, StringComparer.Ordinal).ToArray()));
    }

    public async Task<ResourceVersion?> ReadVersionAsync(ResourceDownloadCommand command, CancellationToken token)
    {
        if ((command.ProjectId.Length > 0 && !Identifier(command.ProjectId)) || !Identifier(command.VersionId)) throw new ArgumentException("资源版本标识无效。");
        using var document = await ReadAsync("version/" + command.VersionId, token, command.MirrorFirst).ConfigureAwait(false);
        var item = document.RootElement;
        if (Text(item, "id") != command.VersionId || (command.ProjectId.Length > 0 && Text(item, "project_id") != command.ProjectId)) throw new InvalidDataException("资源版本不属于此项目。");
        return Version(item, Text(item, "project_id"));
    }

    public async Task<ResourceChangelog> ReadChangelogAsync(ResourceChangelogQuery query, CancellationToken token)
    {
        if (query.Source.Provider != ResourceProvider.Modrinth || !Identifier(query.Source.ProjectId) || !Identifier(query.VersionId)) throw new ArgumentException("更新日志标识无效。");
        using var document = await ReadAsync("version/" + query.VersionId, token, query.MirrorFirst).ConfigureAwait(false);
        var item = document.RootElement;
        if (Text(item, "id") != query.VersionId || Text(item, "project_id") != query.Source.ProjectId) throw new InvalidDataException("更新日志版本身份不匹配。");
        string value = item.TryGetProperty("changelog", out var log) && log.ValueKind == JsonValueKind.String ? log.GetString() ?? "" : "";
        return new(value[..Math.Min(value.Length, 65536)]);
    }

    private async Task<JsonDocument> ReadAsync(string path, CancellationToken token, bool mirrorFirst = true)
    {
        if (transport is not null) return await transport.ReadAsync(ResourceProvider.Modrinth, path, mirrorFirst, token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.modrinth.com/v2/" + path);
        request.Headers.UserAgent.ParseAdd("NexaCL/2.0 (https://github.com/PCL-N-Edition/PCL-N)");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException("资源站响应过大。");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384];
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaxResponseBytes - output.Length + 1)), token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaxResponseBytes) throw new InvalidDataException("资源站响应过大。");
            output.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(output.GetBuffer().AsMemory(0, (int)output.Length), new() { MaxDepth = 32 });
    }

    private static ResourceProject Project(JsonElement item, string id) => new(id, Text(item, "title"),
        Text(item, "description"), Text(item, "author"), Number(item, "downloads"), "https://modrinth.com/project/" + id)
    { IsLibrary = Strings(item, "categories").Any(c => c is "library" or "library-api"), Slug = Text(item, "slug"), Sources = [new(ResourceProvider.Modrinth, id)], IconUrl = ResourceIconService.IsAllowed(Text(item, "icon_url")) ? Text(item, "icon_url") : null };
    private static ResourceVersion Version(JsonElement item, string project) => new(Text(item, "id"), Text(item, "name"), Text(item, "version_number"),
        Text(item, "version_type") switch { "release" => "正式版", "beta" => "Beta", "alpha" => "Alpha", _ => "未标注" }, Strings(item, "game_versions"), Strings(item, "loaders"),
        Text(item, "date_published"), $"https://modrinth.com/project/{project}/version/{Text(item, "id")}")
    {
        Provider = ResourceProvider.Modrinth,
        ProjectId = project,
        Changelog = Text(item, "changelog"),
        File = File(item),
        Dependencies = item.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array
        ? deps.EnumerateArray().Take(257).Select(d => new ResourceDependency(Text(d, "project_id"), Text(d, "version_id"), Text(d, "dependency_type"))).ToArray() : []
    };
    private static ResourceFile? File(JsonElement item)
    {
        if (!item.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return null;
        var list = files.EnumerateArray().Take(100).ToArray();
        var file = list.FirstOrDefault(f => f.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True);
        if (file.ValueKind == JsonValueKind.Undefined) file = list.FirstOrDefault();
        if (file.ValueKind != JsonValueKind.Object) return null;
        var hash = file.TryGetProperty("hashes", out var hashes) ? hashes : default;
        return new(Text(file, "filename"), Text(file, "url"), Number(file, "size"), Text(hash, "sha1"), Text(hash, "sha512"));
    }
    private static bool Identifier(string value) => value.Length is > 0 and <= 64 && value.All(char.IsAsciiLetterOrDigit);
    private static void ValidateFilter(string value, int max)
    {
        if (value.Length > max || value.Any(char.IsControl)) throw new ArgumentException("筛选文字过长或包含无效字符。");
    }
    private static string Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        ? (value.GetString() ?? "")[..Math.Min(value.GetString()!.Length, 2000)] : "";
    private static long Number(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.TryGetInt64(out long result) ? Math.Max(0, result) : 0;
    private static System.Collections.ObjectModel.ReadOnlyCollection<string> Strings(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
        ? Array.AsReadOnly(value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Take(300).Select(item => item.GetString()!).ToArray()) : [];
}
