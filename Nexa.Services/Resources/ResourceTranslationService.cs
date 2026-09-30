using static Nexa.Services.Resources.ResourceProviderHttp;
namespace Nexa.Services.Resources;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Semaphore has no wait handle; it remains alive until in-flight translation waiters release it.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Semaphore has no wait handle; it remains alive until in-flight waiters release it.")]
public sealed class ResourceTranslationService(ResourceProviderHttp http)
{
    private readonly SemaphoreSlim _slots = new(3);
    private readonly Dictionary<ResourceTranslationQuery, ResourceTranslation> _cache = [];
    public async Task<ResourceTranslation> ReadAsync(ResourceTranslationQuery query, CancellationToken token)
    {
        if (query.Original.Length is 0 or > 2000 || query.Source.ProjectId.Length is 0 or > 64 || !query.Source.ProjectId.All(char.IsAsciiLetterOrDigit)) return new(null);
        lock (_cache) if (_cache.TryGetValue(query, out var cached)) return cached;
        await _slots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_cache) if (_cache.TryGetValue(query, out var cached)) return cached;
            string route = query.Source.Provider == ResourceProvider.Modrinth ? "modrinth?project_id=" : "curseforge?mod_id=";
            using var doc = await http.GetAsync(Mirror + "/translate/" + route + query.Source.ProjectId, token).ConfigureAwait(false);
            string translated = Text(doc.RootElement, "translated");
            var result = new ResourceTranslation(Text(doc.RootElement, "original") == query.Original && translated.Length is > 0 and <= 2000 ? translated : null);
            lock (_cache) { if (_cache.Count >= 256) _cache.Remove(_cache.Keys.First()); _cache[query] = result; }
            return result;
        }
        catch (Exception e) when (!token.IsCancellationRequested && e is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException) { return new(null); }
        finally { _slots.Release(); }
    }
}
