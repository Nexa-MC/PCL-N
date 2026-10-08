using System.Text.Json.Nodes;
using Nexa.Services.Caching;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;

namespace Nexa.Services.Capabilities;

internal sealed class InstalledLoaderCompatibility(IInstallCatalogSource? source = null, ISharedStateCache? sharedCache = null)
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly IInstallCatalogSource DefaultSource = new HttpInstallCatalogSource(Http);

    internal async Task<bool?> EvaluateAsync(MinecraftResolvedVersionManifests manifests, MinecraftModLoaderDescriptor loader, CancellationToken token)
    {
        if (loader.Kind == MinecraftModLoaderKind.Vanilla) return true;
        InstallLoader? kind = loader.Kind switch
        {
            MinecraftModLoaderKind.Forge => InstallLoader.Forge,
            MinecraftModLoaderKind.NeoForge => InstallLoader.NeoForge,
            MinecraftModLoaderKind.Fabric => HasLibrary(manifests, "net.legacyfabric:fabric-loader:") ? InstallLoader.LegacyFabric : InstallLoader.Fabric,
            MinecraftModLoaderKind.Quilt => InstallLoader.Quilt,
            MinecraftModLoaderKind.Cleanroom => InstallLoader.Cleanroom,
            MinecraftModLoaderKind.OptiFine => InstallLoader.OptiFine,
            MinecraftModLoaderKind.LiteLoader => InstallLoader.LiteLoader,
            MinecraftModLoaderKind.LabyMod => InstallLoader.LabyMod,
            _ => null
        };
        string? game = (manifests.Inherited.Count > 0 ? manifests.Inherited[^1]["id"]?.ToString() : null)
            ?? manifests.Current["_minecraftVersion"]?.ToString() ?? manifests.Current["clientVersion"]?.ToString()
            ?? manifests.Current["jar"]?.ToString();
        if (kind is null || string.IsNullOrWhiteSpace(game) || game.Length > 96 || string.IsNullOrWhiteSpace(loader.Version)) return null;
        // Forge's Maven coordinate explicitly embeds its Minecraft target. The receipt alone
        // is not evidence: it can be stale after the version JSON was edited by another tool.
        if (kind is InstallLoader.Forge or InstallLoader.NeoForge)
        {
            foreach (string name in Libraries(manifests))
            {
                string[] parts = name.Split(':');
                if (parts.Length < 3 || parts[1] != "forge" || parts[0] is not ("net.minecraftforge" or "net.neoforged")) continue;
                int dash = parts[2].IndexOf('-');
                if (dash > 0 && parts[2][..dash] != game) return false;
            }
        }
        IReadOnlyList<InstallCatalogVersion> versions;
        try
        {
            if (source is null && sharedCache is not null)
                versions = await sharedCache.GetOrCreateAsync<IReadOnlyList<InstallCatalogVersion>>(
                    new("minecraft.install-catalog", game + "|" + kind.Value,
                        "official-install-providers-v1|" + RegionalPolicy.Current.CountryCode),
                    InstallCatalogInformationCache.Policy, FetchAsync,
                    shouldStore: InstallCatalogInformationCache.Positive, cancellationToken: token).ConfigureAwait(false);
            else versions = await FetchAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException or System.Xml.XmlException or InvalidOperationException) { return null; }
        return versions.Any(item => string.Equals(item.Id, loader.Version, StringComparison.Ordinal)) ? true : null;

        async ValueTask<IReadOnlyList<InstallCatalogVersion>> FetchAsync(CancellationToken cancellation)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            IReadOnlyList<InstallCatalogVersion> fetched = await (source ?? DefaultSource)
                .GetLoadersAsync(kind.Value, game, deadline.Token).ConfigureAwait(false);
            return Array.AsReadOnly(fetched.Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .DistinctBy(item => item.Id, StringComparer.Ordinal).ToArray());
        }
    }

    private static bool HasLibrary(MinecraftResolvedVersionManifests manifests, string prefix) => Libraries(manifests).Any(name => name.StartsWith(prefix, StringComparison.Ordinal));
    private static IEnumerable<string> Libraries(MinecraftResolvedVersionManifests manifests) =>
        new[] { manifests.Current }.Concat(manifests.Inherited).SelectMany(item => item["libraries"] as JsonArray ?? [])
            .OfType<JsonObject>().Select(item => item["name"]?.ToString() ?? "");
}
