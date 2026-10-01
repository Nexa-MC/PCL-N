using System.Text.Json;

namespace Nexa.Services.Resources;

internal sealed class ChineseResourceIndex
{
    internal sealed record Entry(int Wiki, string Name, string CurseForge, string Modrinth);
    internal static readonly Lazy<ChineseResourceIndex> Shared = new(() => new());
    private readonly Entry[] _entries;
    private readonly Dictionary<char, int[]> _characters;
    private readonly Dictionary<string, Entry> _mr = new(StringComparer.OrdinalIgnoreCase), _cf = new(StringComparer.OrdinalIgnoreCase);
    private ChineseResourceIndex()
    {
        using var stream = typeof(ChineseResourceIndex).Assembly.GetManifestResourceStream("Nexa.Services.Resources.ChineseNames.json")!;
        using var document = JsonDocument.Parse(stream);
        _entries = document.RootElement.EnumerateArray().Select(row => new Entry(row[0].GetInt32(), row[1].GetString()!, row[2].GetString()!, row[3].GetString()!)).ToArray();
        _entries = _entries.OrderBy(entry => entry.Name.Length).ThenBy(entry => entry.Wiki).ToArray();
        var characters = new Dictionary<char, List<int>>();
        for (int i = 0; i < _entries.Length; i++)
            foreach (char character in _entries[i].Name.Distinct())
                if (character is >= '\u3400' and <= '\u9fff')
                {
                    if (!characters.TryGetValue(character, out var positions)) characters[character] = positions = [];
                    positions.Add(i);
                }
        _characters = characters.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        foreach (var entry in _entries)
        {
            if (entry.Modrinth.Length > 0) _mr.TryAdd(entry.Modrinth, entry);
            if (entry.CurseForge.Length > 0) _cf.TryAdd(entry.CurseForge, entry);
        }
    }
    internal ResourceProject Decorate(ResourceProject project)
    {
        var map = (project.Sources.Count == 0 ? ResourceProvider.Modrinth : project.Sources[0].Provider) == ResourceProvider.CurseForge ? _cf : _mr;
        return map.TryGetValue(project.Slug, out var entry) ? project with { ChineseName = entry.Name, WikiId = entry.Wiki } : project;
    }
    internal bool SameProject(ResourceProject left, ResourceProject right)
    {
        if (left.Sources.Count == 0 || right.Sources.Count == 0 || left.Sources[0].Provider == right.Sources[0].Provider) return false;
        var mr = left.Sources[0].Provider == ResourceProvider.Modrinth ? left : right;
        var cf = left.Sources[0].Provider == ResourceProvider.CurseForge ? left : right;
        return _mr.TryGetValue(mr.Slug, out var entry) && entry.CurseForge.Length > 0 && string.Equals(entry.CurseForge, cf.Slug, StringComparison.OrdinalIgnoreCase);
    }
    internal string MergeKey(ResourceProject project)
    {
        if (project.Sources.Count == 0) return "unknown:" + project.Id;
        var source = project.Sources[0];
        var map = source.Provider == ResourceProvider.Modrinth ? _mr : _cf;
        return map.TryGetValue(project.Slug, out var entry) && entry.CurseForge.Length > 0 && entry.Modrinth.Length > 0
            ? "pair:" + entry.CurseForge + "\0" + entry.Modrinth : source.Provider + ":" + source.ProjectId;
    }
    internal Entry[] Find(string query)
    {
        var characters = query.Where(c => c is >= '\u3400' and <= '\u9fff').Distinct().ToArray();
        if (characters.Length == 0 || characters.Any(c => !_characters.ContainsKey(c))) return [];
        return characters.Select(c => _characters[c]).MinBy(indices => indices.Length)!
            .Select(i => _entries[i]).Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(4).ToArray();
    }
}
