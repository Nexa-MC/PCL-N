using System.Text.Json;
using static Nexa.Services.Resources.ResourceProviderHttp;

namespace Nexa.Services.Resources;

public sealed class CurseForgeResourceCatalog(ResourceProviderHttp transport) : IResourceCatalogSource, IResourceFileSource
{
    private static string Loader(string value) => value.ToLowerInvariant() switch { "forge" => "1", "fabric" => "4", "quilt" => "5", "neoforge" => "6", "" or "datapack" => "", _ => "unsupported" };
    public async Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token)
    {
        Validate(query.Text, 200); Validate(query.GameVersion, 40); Validate(query.Loader, 40);
        if (query.Page is < 0 or > 499 || !Enum.IsDefined(query.Kind) || !Enum.IsDefined(query.Order)) throw new ArgumentException("资源筛选无效。");
        string loader = Loader(query.Loader);
        if (loader == "unsupported") return new([], 0, query.Page);
        int category = query.Kind switch { ResourceKind.Mod => 6, ResourceKind.Modpack => 4471, ResourceKind.ResourcePack => 12, ResourceKind.Shader => 6552, _ => 6945 };
        int order = query.Order switch { ResourceOrder.Downloads => 6, ResourceOrder.Updated => 3, _ => 2 };
        using var doc = await transport.ReadAsync(ResourceProvider.CurseForge,
            $"mods/search?gameId=432&classId={category}&pageSize=20&index={query.Page * 20}&sortOrder=desc&sortField={order}&searchFilter={Uri.EscapeDataString(query.Text)}" + Filters(query.GameVersion, loader), query.MirrorFirst, token).ConfigureAwait(false);
        var data = doc.RootElement.GetProperty("data");
        int total = doc.RootElement.TryGetProperty("pagination", out var page) ? (int)Math.Clamp(Number(page, "totalCount"), 0, 10000) : data.GetArrayLength();
        return new(data.EnumerateArray().Take(20).Select(item => Project(item) with { Kind = query.Kind }).ToArray(), total, query.Page);
    }
    public async Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token)
    {
        if (!Id(query.ProjectId)) throw new ArgumentException("CurseForge 项目标识无效。");
        Validate(query.GameVersion, 40); Validate(query.Loader, 40);
        using var project = await transport.ReadAsync(ResourceProvider.CurseForge, "mods/" + query.ProjectId, query.MirrorFirst, token).ConfigureAwait(false);
        var p = Project(project.RootElement.GetProperty("data"));
        if (p.Id != query.ProjectId) throw new InvalidDataException("资源详情标识不匹配。");
        string loader = Loader(query.Loader);
        if (loader == "unsupported") return new(p, "", []);
        List<ResourceVersion> versions = [];
        for (int offset = 0; offset < 200; offset += 50)
        {
            using var files = await transport.ReadAsync(ResourceProvider.CurseForge, $"mods/{query.ProjectId}/files?pageSize=50&index={offset}" + Filters(query.GameVersion, loader), query.MirrorFirst, token).ConfigureAwait(false);
            var data = files.RootElement.GetProperty("data");
            foreach (var f in data.EnumerateArray().Take(50))
            {
                if (Text(f, "modId") != query.ProjectId || !Id(Text(f, "id"))) continue;
                var games = f.GetProperty("gameVersions").EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
                if (query.GameVersion.Length > 0 && !games.Contains(query.GameVersion)) continue;
                if (loader.Length > 0 && !games.Contains(query.Loader, StringComparer.OrdinalIgnoreCase)) continue;
                string id = Text(f, "id"), url = Text(f, "downloadUrl"), sha1 = "";
                if (f.TryGetProperty("hashes", out var hashes)) sha1 = hashes.EnumerateArray().Where(h => Number(h, "algo") == 1).Select(h => Text(h, "value")).FirstOrDefault() ?? "";
                bool available = !f.TryGetProperty("isAvailable", out var enabled) || enabled.ValueKind == JsonValueKind.True;
                versions.Add(new(id, Text(f, "displayName"), Text(f, "fileName"), Number(f, "releaseType") switch { 1 => "正式版", 2 => "Beta", _ => "Alpha" },
                    games.Where(g => g.Length > 0 && char.IsAsciiDigit(g[0])).ToArray(), games.Where(g => Loader(g) is not ("" or "unsupported")).ToArray(), Text(f, "fileDate"), p.Website + "/files/" + id)
                { Provider = ResourceProvider.CurseForge, ProjectId = query.ProjectId, Dependencies = Dependencies(f), File = available && url.Length > 0 ? new(Text(f, "fileName"), url, Number(f, "fileLength"), sha1, null) : null });
            }
            if (data.GetArrayLength() < 50) break;
        }
        return new(p, "", versions.OrderByDescending(v => v.Published, StringComparer.Ordinal).ToArray())
        { Notice = versions.Count >= 200 ? "已显示前 200 个版本，更多版本可前往项目主页查看。" : null };
    }
    public async Task<ResourceVersion?> ReadVersionAsync(ResourceDownloadCommand command, CancellationToken token)
    {
        if (!Id(command.ProjectId) || !Id(command.VersionId)) throw new ArgumentException("CurseForge 文件标识无效。");
        using var document = await transport.ReadAsync(ResourceProvider.CurseForge, $"mods/{command.ProjectId}/files/{command.VersionId}", command.MirrorFirst, token).ConfigureAwait(false);
        var file = document.RootElement.GetProperty("data");
        if (Text(file, "id") != command.VersionId || Text(file, "modId") != command.ProjectId) throw new InvalidDataException("资源版本不属于此项目。");
        if (Text(file, "downloadUrl").Length == 0 || file.TryGetProperty("isAvailable", out var available) && available.ValueKind != JsonValueKind.True) return null;
        string sha1 = file.TryGetProperty("hashes", out var hashes) ? hashes.EnumerateArray().Where(h => Number(h, "algo") == 1).Select(h => Text(h, "value")).FirstOrDefault() ?? "" : "";
        return new(command.VersionId, Text(file, "displayName"), Text(file, "fileName"), Number(file, "releaseType") switch { 1 => "正式版", 2 => "Beta", _ => "Alpha" },
            file.GetProperty("gameVersions").EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length > 0 && char.IsAsciiDigit(v[0])).ToArray(),
            file.GetProperty("gameVersions").EnumerateArray().Select(v => v.GetString() ?? "").Where(v => Loader(v) is not ("" or "unsupported")).ToArray(), Text(file, "fileDate"), "https://www.curseforge.com/projects/" + command.ProjectId)
        { Provider = ResourceProvider.CurseForge, ProjectId = command.ProjectId, File = new(Text(file, "fileName"), Text(file, "downloadUrl"), Number(file, "fileLength"), sha1, null), Dependencies = Dependencies(file) };
    }
    private static ResourceDependency[] Dependencies(JsonElement file) => file.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array
        ? deps.EnumerateArray().Take(257).Select(d => new ResourceDependency(Text(d, "modId"), null, Number(d, "relationType") switch { 1 or 6 => "embedded", 3 => "required", 5 => "incompatible", _ => "optional" })).ToArray() : [];
    private static ResourceProject Project(JsonElement item)
    {
        string id = Text(item, "id");
        if (!Id(id)) throw new InvalidDataException("CurseForge 项目标识无效。");
        string icon = item.TryGetProperty("logo", out var logo) ? Text(logo, "thumbnailUrl") : "";
        string website = item.TryGetProperty("links", out var links) ? Text(links, "websiteUrl") : "";
        if (!Uri.TryCreate(website, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "www.curseforge.com" || !uri.IsDefaultPort || uri.UserInfo.Length > 0) website = "https://www.curseforge.com/projects/" + id;
        return new(id, Text(item, "name"), Text(item, "summary"), item.TryGetProperty("authors", out var authors) ? string.Join(", ", authors.EnumerateArray().Take(8).Select(a => Text(a, "name"))) : "", Number(item, "downloadCount"), website.TrimEnd('/'))
        { IsLibrary = item.TryGetProperty("categories", out var categories) && categories.EnumerateArray().Any(c => Number(c, "id") == 421 || Text(c, "slug") == "library-api"), Slug = Text(item, "slug"), Sources = [new(ResourceProvider.CurseForge, id)], IconUrl = ResourceIconService.IsAllowed(icon) ? icon : null };
    }
    private static string Filters(string game, string loader) => (game.Length == 0 ? "" : "&gameVersion=" + Uri.EscapeDataString(game)) + (loader.Length == 0 ? "" : "&modLoaderType=" + loader);
    private static bool Id(string id) => id.Length is > 0 and < 20 && id.All(char.IsAsciiDigit);
    private static void Validate(string value, int max) { if (value.Length > max || value.Any(char.IsControl)) throw new ArgumentException("筛选条件无效。"); }
}
