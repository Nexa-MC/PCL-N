using System.Text.Json.Nodes;
namespace Nexa.Services.Resources;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The asynchronous mutex is kept alive for outstanding requests and never opens a wait handle.")]
public sealed class ResourceFavoritesService(string? storagePath)
{
    private readonly SemaphoreSlim _gate = new(1);
    private List<ResourceProject>? _items;
    public Task<ResourceFavoritesSnapshot> ReadAsync(CancellationToken token) => Task.Run(() => ReadCoreAsync(token), token);
    private async Task<ResourceFavoritesSnapshot> ReadCoreAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { await LoadAsync(token).ConfigureAwait(false); return new(_items!.ToArray()); }
        finally { _gate.Release(); }
    }
    public Task SetAsync(ResourceFavoriteCommand command, CancellationToken token) => Task.Run(() => SetCoreAsync(command, token), token);
    private async Task SetCoreAsync(ResourceFavoriteCommand command, CancellationToken token)
    {
        if (command.Project.Sources.Count is 0 or > 2 || command.Project.Sources.Any(s => !Enum.IsDefined(s.Provider) || s.ProjectId.Length is 0 or > 64 || !s.ProjectId.All(char.IsAsciiLetterOrDigit))) throw new ArgumentException("收藏项目标识无效。");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await LoadAsync(token).ConfigureAwait(false);
            var next = _items!.Where(p => !p.Sources.Intersect(command.Project.Sources).Any()).ToList();
            if (command.Saved) next.Insert(0, command.Project);
            if (next.Count > 1000) throw new InvalidDataException("收藏夹最多保存 1000 项。");
            if (storagePath is not null)
            {
                var array = new JsonArray();
                foreach (var p in next)
                {
                    var sources = new JsonArray(p.Sources.Select(s => (JsonNode?)new JsonObject { ["provider"] = (int)s.Provider, ["id"] = s.ProjectId }).ToArray());
                    array.Add((JsonNode)new JsonObject
                    {
                        ["kind"] = (int)p.Kind,
                        ["id"] = p.Id,
                        ["title"] = Clip(p.Title),
                        ["description"] = Clip(p.Description),
                        ["author"] = Clip(p.Author),
                        ["downloads"] = p.Downloads,
                        ["slug"] = p.Slug,
                        ["chinese"] = Clip(p.ChineseName ?? ""),
                        ["icon"] = p.IconUrl,
                        ["library"] = p.IsLibrary,
                        ["sources"] = sources
                    });
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storagePath))!);
                string temporary = storagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllTextAsync(temporary, array.ToJsonString(), token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); File.Move(temporary, storagePath, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            _items = next;
        }
        finally { _gate.Release(); }
    }
    private async Task LoadAsync(CancellationToken token)
    {
        if (_items is not null) return;
        List<ResourceProject> items = [];
        if (storagePath is not null && File.Exists(storagePath))
        {
            await using var input = File.OpenRead(storagePath); using var buffer = new MemoryStream();
            await Nexa.Services.Files.ArchiveReadBudget.CopyAsync(input, buffer, input.Length, 8 * 1024 * 1024, new(8 * 1024 * 1024), token).ConfigureAwait(false);
            var array = JsonNode.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), documentOptions: new() { MaxDepth = 8 }) as JsonArray ?? throw new InvalidDataException("收藏文件无效。");
            foreach (var node in array.Take(1000))
            {
                if (node is not JsonObject p || p["sources"] is not JsonArray refs) continue;
                var sources = refs.OfType<JsonObject>().Take(2).Select(s => new ResourceReference((ResourceProvider)(s["provider"]?.GetValue<int>() ?? -1), s["id"]?.ToString() ?? "")).ToArray();
                if (sources.Length == 0 || sources.Any(s => !Enum.IsDefined(s.Provider) || s.ProjectId.Length is 0 or > 64 || !s.ProjectId.All(char.IsAsciiLetterOrDigit))) continue;
                var source = sources[0];
                string website = source.Provider == ResourceProvider.Modrinth ? "https://modrinth.com/project/" : "https://www.curseforge.com/projects/";
                string icon = p["icon"]?.ToString() ?? "";
                items.Add(new(source.ProjectId, Clip(p["title"]?.ToString() ?? ""), Clip(p["description"]?.ToString() ?? ""), Clip(p["author"]?.ToString() ?? ""), p["downloads"]?.GetValue<long>() ?? 0, website + source.ProjectId)
                { Kind = (ResourceKind)(p["kind"]?.GetValue<int>() ?? 0), Sources = sources, Slug = p["slug"]?.ToString() ?? "", ChineseName = Clip(p["chinese"]?.ToString() ?? ""), IconUrl = ResourceIconService.IsAllowed(icon) ? icon : null, IsLibrary = p["library"]?.GetValue<bool>() ?? false });
            }
        }
        _items = items;
    }
    private static string Clip(string value) => value[..Math.Min(value.Length, 2000)];
}
