using static Nexa.Services.Resources.ResourceProviderHttp;
namespace Nexa.Services.Resources;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Semaphore has no wait handle; it remains alive until in-flight translation owners release it.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213", Justification = "SemaphoreSlim never creates a wait handle here and must remain valid until outstanding translation owners release it.")]
public sealed class ResourceTranslationService(ResourceProviderHttp http, ResourceOnlineInformationCache? information = null) : IDisposable
{
    public ResourceTranslationService(ResourceProviderHttp http) : this(http, null) { }
    private readonly SemaphoreSlim _slots = new(3);
    private readonly ResourceOnlineInformationCache _information = information ?? new();
    public void Dispose() { if (information is null) _information.Dispose(); }
    public Task<ResourceTranslation> ReadAsync(ResourceTranslationQuery query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(query.Source.Provider) || query.Original.Length is 0 or > 2000 || query.Source.ProjectId.Length is 0 or > 64 || !query.Source.ProjectId.All(query.Source.Provider == ResourceProvider.Modrinth ? char.IsAsciiLetterOrDigit : char.IsAsciiDigit)) return Task.FromResult(new ResourceTranslation(null));
        string identity = ResourceOnlineInformationCache.Identity(http.CachePolicyIdentity, query.Source.Provider.ToString(), query.Source.ProjectId, query.Original);
        return _information.ReadAsync("translation", identity, ResourceOnlineInformationCache.MetadataFresh, ResourceOnlineInformationCache.MetadataRetain,
            4096, ResourceMetadataJsonContext.Default.ResourceMetadataEnvelopeResourceTranslation,
            ct => ReadCoreAsync(query, ct), value => !string.IsNullOrWhiteSpace(value.Description), value => value,
            value => value with { Notice = ResourceOnlineInformationCache.StaleNotice }, token: token);
    }
    private async Task<ResourceTranslation> ReadCoreAsync(ResourceTranslationQuery query, CancellationToken token)
    {
        await _slots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string route = query.Source.Provider == ResourceProvider.Modrinth ? "modrinth?project_id=" : "curseforge?mod_id=";
            using var doc = await http.GetAsync(Mirror + "/translate/" + route + query.Source.ProjectId, token).ConfigureAwait(false);
            string translated = Text(doc.RootElement, "translated");
            return new(Text(doc.RootElement, "original") == query.Original && translated.Length is > 0 and <= 2000 ? translated : null);
        }
        catch (Exception e) when (!token.IsCancellationRequested && ResourceOnlineInformationCache.Recoverable(e)) { return new(null); }
        finally { _slots.Release(); }
    }
}
