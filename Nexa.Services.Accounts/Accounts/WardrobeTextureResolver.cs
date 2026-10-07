using System.Text.Json;
using Nexa.Core.Media;

namespace Nexa.Services.Accounts;

/// <summary>Best-effort public skin, cape and model resolution, without authentication headers or UI I/O.</summary>
public sealed class WardrobeTextureResolver(HttpClient client)
{
    private const int MetadataLimit = 128 * 1024;
    private const int ImageLimit = 1_048_576;
    private static readonly SemaphoreSlim ReadGate = new(3, 3);

    public ValueTask<AccountWardrobeResolvedTextures> ResolveAsync(LaunchProfileView profile,
        CancellationToken cancellationToken = default) => ResolveCoreAsync(profile, includeImages: true, cancellationToken);

    /// <summary>Metadata-only history lookup, bounded to two seconds; no encoded image downloads.</summary>
    public ValueTask<AccountWardrobeResolvedTextures> ResolveReferencesAsync(LaunchProfileView profile,
        CancellationToken cancellationToken = default) => ResolveCoreAsync(profile, includeImages: false, cancellationToken);

    private async ValueTask<AccountWardrobeResolvedTextures> ResolveCoreAsync(LaunchProfileView profile,
        bool includeImages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? skinAddress = SafeAddress(profile.SkinAddress);
        string? capeAddress = null;
        bool slim = profile.Kind == LaunchProfileKind.Offline && OfflineIsSlim(profile.Uuid);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(includeImages ? 30 : 2));
        if (CreateSessionAddress(profile) is { } metadataAddress)
        {
            try
            {
                byte[] bytes = await ReadAsync(new Uri(metadataAddress), MetadataLimit, deadline.Token).ConfigureAwait(false);
                (string? Skin, string? Cape, bool? Slim) textures = ParseTextures(bytes, profile.Uuid);
                skinAddress = textures.Skin ?? skinAddress;
                capeAddress = textures.Cape;
                slim = textures.Slim ?? slim;
            }
            catch (Exception error) when (Recoverable(error) && !cancellationToken.IsCancellationRequested) { }
        }

        if (!includeImages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(skinAddress, capeAddress, slim, null, null);
        }

        PngImage? skin = null;
        PngImage? cape = null;
        // A direct legacy address can itself be a public session-profile document. Follow one reference only.
        if (skinAddress is not null && !deadline.IsCancellationRequested)
        {
            try
            {
                byte[] bytes = await ReadAsync(new Uri(skinAddress), ImageLimit, deadline.Token).ConfigureAwait(false);
                if (IsPng(bytes)) skin = WardrobeSkinValidator.ValidateTexture(bytes, AccountWardrobeTextureKind.Skin, slim);
                else if (bytes.Length <= MetadataLimit)
                {
                    var textures = ParseTextures(bytes, profile.Uuid);
                    skinAddress = textures.Skin;
                    capeAddress = textures.Cape ?? capeAddress;
                    slim = textures.Slim ?? slim;
                    if (skinAddress is not null)
                        skin = await ReadImageAsync(skinAddress, AccountWardrobeTextureKind.Skin, slim, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (Recoverable(error) && !cancellationToken.IsCancellationRequested) { }
        }
        if (capeAddress is not null && !deadline.IsCancellationRequested)
        {
            try { cape = await ReadImageAsync(capeAddress, AccountWardrobeTextureKind.Cape, false, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(skinAddress, capeAddress, slim, skin, cape);
    }

    /// <summary>Resolves at most 64 profiles, with three public HTTP reads in flight across resolver instances.</summary>
    public async ValueTask<IReadOnlyList<AccountWardrobeResolvedTextures>> ResolveAllAsync(
        IReadOnlyList<LaunchProfileView> profiles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        int count = Math.Min(64, profiles.Count);
        AccountWardrobeResolvedTextures[] result = new AccountWardrobeResolvedTextures[count];
        await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions
        { MaxDegreeOfParallelism = 3, CancellationToken = cancellationToken }, async (index, token) =>
            result[index] = await ResolveAsync(profiles[index], token).ConfigureAwait(false)).ConfigureAwait(false);
        return Array.AsReadOnly(result);
    }

    /// <summary>Returns a complete immutable PNG, or null for unavailable/malformed public textures.</summary>
    public async ValueTask<PngImage?> ReadImageAsync(string address, AccountWardrobeTextureKind kind,
        bool isSlim = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SafeAddress(address) is not { } safe || kind is not (AccountWardrobeTextureKind.Skin or AccountWardrobeTextureKind.Cape)) return null;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            byte[] bytes = await ReadAsync(new Uri(safe), ImageLimit, deadline.Token).ConfigureAwait(false);
            PngImage image = WardrobeSkinValidator.ValidateTexture(bytes, kind, isSlim);
            cancellationToken.ThrowIfCancellationRequested();
            return image;
        }
        catch (Exception error) when (Recoverable(error) && !cancellationToken.IsCancellationRequested) { return null; }
    }

    /// <summary>Public HTTPS only. Legacy Minecraft/LittleSkin HTTP URLs are upgraded before use.</summary>
    public static string? SafeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 2048
            || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri? uri) || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.IsLoopback || !uri.IsDefaultPort) return null;
        if (uri.Scheme == "http" && uri.Host is "textures.minecraft.net" or "littleskin.cn")
            uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.HostNameType != UriHostNameType.Dns
            || !uri.Host.Contains('.') || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)) return null;
        return uri.AbsoluteUri;
    }

    private static string? CreateSessionAddress(LaunchProfileView profile)
    {
        if (profile.Kind == LaunchProfileKind.Offline) return null;
        string identity = profile.SkinAddress?.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) == true
            ? profile.SkinAddress[5..] : profile.Uuid;
        if (!Guid.TryParse(identity, out Guid uuid)) return null;
        string authServer = string.IsNullOrWhiteSpace(profile.AuthServer) && profile.Kind == LaunchProfileKind.LittleSkin
            ? LittleSkinOAuthService.YggdrasilServer : profile.AuthServer;
        string endpoint = string.IsNullOrWhiteSpace(authServer)
            ? "https://sessionserver.mojang.com/session/minecraft/profile/" + uuid.ToString("N")
            : authServer.TrimEnd('/') + "/sessionserver/session/minecraft/profile/" + uuid.ToString("N");
        return SafeAddress(endpoint);
    }

    private static (string? Skin, string? Cape, bool? Slim) ParseTextures(byte[] bytes, string expectedUuid)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return default;
        if (!MatchesIdentity(root, "id", expectedUuid) || !MatchesIdentity(root, "profileId", expectedUuid)) return default;
        if (root.TryGetProperty("textures", out JsonElement direct)) return ParseTextureEntries(direct);
        if (!root.TryGetProperty("properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Array) return default;
        foreach (JsonElement property in properties.EnumerateArray().Take(64))
        {
            if (property.ValueKind != JsonValueKind.Object || !property.TryGetProperty("name", out JsonElement name)
                || name.ValueKind != JsonValueKind.String || !string.Equals(name.GetString(), "textures", StringComparison.OrdinalIgnoreCase)
                || !property.TryGetProperty("value", out JsonElement value) || value.ValueKind != JsonValueKind.String
                || value.GetString() is not { Length: > 0 and <= MetadataLimit } encoded) continue;
            byte[] decoded = Convert.FromBase64String(encoded);
            if (decoded.Length > MetadataLimit) return default;
            using JsonDocument textures = JsonDocument.Parse(decoded, new JsonDocumentOptions { MaxDepth = 16 });
            if (textures.RootElement.ValueKind == JsonValueKind.Object
                && MatchesIdentity(textures.RootElement, "profileId", expectedUuid)
                && textures.RootElement.TryGetProperty("textures", out JsonElement entries)) return ParseTextureEntries(entries);
        }
        return default;
    }

    private static bool MatchesIdentity(JsonElement root, string property, string expectedUuid) => !Guid.TryParse(expectedUuid, out Guid expected)
        || !root.TryGetProperty(property, out JsonElement id)
        || id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out Guid actual) && expected == actual;

    private static (string? Skin, string? Cape, bool? Slim) ParseTextureEntries(JsonElement entries)
    {
        if (entries.ValueKind != JsonValueKind.Object) return default;
        string? skin = TextureAddress(entries, "SKIN"), cape = TextureAddress(entries, "CAPE");
        bool? slim = null;
        if (entries.TryGetProperty("SKIN", out JsonElement skinEntry) && skinEntry.ValueKind == JsonValueKind.Object)
        {
            slim = false;
            if (skinEntry.TryGetProperty("metadata", out JsonElement metadata) && metadata.ValueKind == JsonValueKind.Object
                && metadata.TryGetProperty("model", out JsonElement model) && model.ValueKind == JsonValueKind.String)
                slim = string.Equals(model.GetString(), "slim", StringComparison.OrdinalIgnoreCase);
        }
        return (skin, cape, slim);
    }

    private static string? TextureAddress(JsonElement entries, string name) => entries.TryGetProperty(name, out JsonElement texture)
        && texture.ValueKind == JsonValueKind.Object && texture.TryGetProperty("url", out JsonElement address)
        && address.ValueKind == JsonValueKind.String ? SafeAddress(address.GetString()) : null;

    private static bool OfflineIsSlim(string uuid)
    {
        if (!Guid.TryParse(uuid, out Guid parsed)) return false;
        string normalized = parsed.ToString("N");
        return ((HexValue(normalized[7]) ^ HexValue(normalized[15]) ^ HexValue(normalized[23]) ^ HexValue(normalized[31])) & 1) != 0;
    }

    private static int HexValue(char value) => value <= '9' ? value - '0' : value - 'a' + 10;

    private static bool IsPng(byte[] bytes) => bytes.Length >= 8
        && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

    private static bool Recoverable(Exception error) => error is HttpRequestException or IOException or InvalidDataException
        or JsonException or FormatException or InvalidOperationException or ArgumentException or OperationCanceledException;

    private async ValueTask<byte[]> ReadAsync(Uri address, int limit, CancellationToken token)
    {
        // A caller-supplied authenticated client cannot accidentally disclose its default credentials to public texture hosts.
        if (client.DefaultRequestHeaders.Authorization is not null || client.DefaultRequestHeaders.Contains("Cookie"))
            throw new InvalidOperationException("Texture resolution requires a public HTTP client.");
        await ReadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, address);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } actual && SafeAddress(actual.AbsoluteUri) is null)
                throw new InvalidDataException("Texture redirect is not a public HTTPS address.");
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                limit = Math.Min(limit, MetadataLimit);
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Texture response exceeds limit.");
            using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using MemoryStream result = new();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (result.Length + count > limit) throw new InvalidDataException("Texture response exceeds limit.");
                result.Write(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
            return result.ToArray();
        }
        finally { ReadGate.Release(); }
    }
}
