using System.Globalization;
using System.Text.Json;
using Nexa.Xsr;

namespace Nexa.Services.Accounts;

/// <summary>Public, bounded LittleSkin catalogue. It never reads or sends account credentials.</summary>
public sealed class WardrobeCatalogService(HttpClient http, TimeProvider? timeProvider = null)
{
    private const int MaxResponseBytes = 1_048_576;
    private const int MaxPageItems = 64;
    private const int MaxCachedPages = 64;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OperationDeadline = TimeSpan.FromSeconds(30);
    private static readonly WardrobeCatalogSite LittleSkin = new("littleskin", "LittleSkin",
        new("https://littleskin.cn/"), new("https://manual.littlesk.in/advanced/api"), "lucide/shirt", true);
    private readonly IReadOnlyList<WardrobeCatalogSite> _sites = Array.AsReadOnly(new[] { LittleSkin });
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _cacheGate = new();
    private readonly Dictionary<WardrobeCatalogQuery, CacheEntry> _cache = [];
    private long _requestVersion;
    private long _usage;

    public ValueTask<XsrResult<IReadOnlyList<WardrobeCatalogSite>>> SitesAsync(WardrobeCatalogSitesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ValueTask.FromResult(cancellationToken.IsCancellationRequested
            ? XsrResult.Failure<IReadOnlyList<WardrobeCatalogSite>>(XsrRuntimeErrors.Cancelled())
            : XsrResult.Success(_sites));
    }

    public async ValueTask<XsrResult<WardrobeCatalogPage>> ReadAsync(WardrobeCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationDeadline);
        Task<SiteIdentity>? identityTask = null;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            var normalized = Normalize(query);
            long version;
            lock (_cacheGate)
            {
                if (!query.ForceRefresh && _cache.TryGetValue(normalized, out var cached)
                    && cached.Page is not null && Fresh(cached.CreatedUtc))
                {
                    _cache[normalized] = cached with { Usage = ++_usage };
                    return XsrResult.Success(cached.Page);
                }
                version = ++_requestVersion;
                Reserve(normalized, version);
            }

            identityTask = ReadIdentityAsync(deadline.Token);
            using var document = await ReadJsonAsync(BuildListUri(normalized), deadline.Token).ConfigureAwait(false);
            var list = ParseList(document.RootElement, normalized);
            using var detailSlots = new SemaphoreSlim(4, 4);
            var detailTasks = list.Items.Select(item => ResolvePageItemAsync(item, detailSlots, deadline.Token)).ToArray();
            var resolved = await Task.WhenAll(detailTasks).ConfigureAwait(false);
            var identity = await identityTask.ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            var page = new WardrobeCatalogPage(LittleSkin.Id, identity.Name, identity.Version, list.Page,
                list.HasPrevious, list.HasNext, Array.AsReadOnly(resolved.Where(static item => item is not null)
                    .Cast<WardrobeCatalogItem>().Where(item => item.Kind == normalized.Kind).ToArray()));
            lock (_cacheGate)
            {
                // A response from before an explicit refresh cannot replace that refresh's cache slot.
                if (_cache.TryGetValue(normalized, out var reservation) && reservation.Version == version)
                    _cache[normalized] = new(page, _clock.GetUtcNow(), version, ++_usage);
            }
            return XsrResult.Success(page);
        }
        catch (Exception error) when (Recoverable(error))
        {
            deadline.Cancel();
            if (identityTask is not null)
            {
                try { await identityTask.ConfigureAwait(false); }
                catch (Exception identityError) when (Recoverable(identityError)) { }
            }
            return XsrResult.Failure<WardrobeCatalogPage>(Error(error, cancellationToken));
        }
    }

    public ValueTask<XsrResult<WardrobeCatalogItem>> ResolveAsync(WardrobeCatalogResolveQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ResolveItemAsync(query.SiteId, query.TextureId, cancellationToken);
    }

    /// <summary>Reads a bounded, validated public preview after resolving the site's current texture metadata.</summary>
    public async ValueTask<XsrResult<AccountWardrobeResolvedTextures>> PreviewAsync(WardrobeCatalogPreviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationDeadline);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(query.Kind)) throw new InvalidDataException("皮肤库纹理类型无效。");
            var resolved = await ResolveItemAsync(query.SiteId, query.TextureId, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (!resolved.IsSuccess) return XsrResult.Failure<AccountWardrobeResolvedTextures>(resolved.Error!);
            var item = resolved.Value;
            if (item.Kind != query.Kind) throw new InvalidDataException("皮肤站返回的纹理类型不属于此条目。");
            bool isSlim = item.Model == "alex";
            var kind = query.Kind == WardrobeCatalogKind.Cape ? AccountWardrobeTextureKind.Cape : AccountWardrobeTextureKind.Skin;
            var image = await new WardrobeTextureResolver(http).ReadImageAsync(item.TextureAddress, kind, isSlim,
                deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (image is null) throw new InvalidDataException("皮肤站未提供有效的纹理预览。");
            return XsrResult.Success(kind == AccountWardrobeTextureKind.Skin
                ? new AccountWardrobeResolvedTextures(item.TextureAddress, null, isSlim, image, null)
                : new AccountWardrobeResolvedTextures(null, item.TextureAddress, false, null, image));
        }
        catch (Exception error) when (Recoverable(error))
        { return XsrResult.Failure<AccountWardrobeResolvedTextures>(Error(error, cancellationToken)); }
    }

    /// <summary>Resolve the provider's current hash from a fixed site and ID, never a caller-supplied URL.</summary>
    public async ValueTask<XsrResult<WardrobeCatalogItem>> ResolveItemAsync(string siteId, long textureId,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationDeadline);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            DemandSite(siteId);
            if (textureId <= 0) throw new InvalidDataException("皮肤库条目标识无效。");
            var known = KnownItem(textureId);
            using var document = await ReadJsonAsync(DetailUri(textureId), deadline.Token).ConfigureAwait(false);
            var item = ParseDetail(document.RootElement, textureId, known);
            deadline.Token.ThrowIfCancellationRequested();
            return XsrResult.Success(item);
        }
        catch (Exception error) when (Recoverable(error))
        { return XsrResult.Failure<WardrobeCatalogItem>(Error(error, cancellationToken)); }
    }

    private bool Fresh(DateTimeOffset createdUtc) => _clock.GetUtcNow() - createdUtc is var elapsed
        && elapsed >= TimeSpan.Zero && elapsed < CacheLifetime;

    private void Reserve(WardrobeCatalogQuery query, long version)
    {
        if (!_cache.ContainsKey(query) && _cache.Count >= MaxCachedPages)
        {
            var oldest = _cache.MinBy(static pair => pair.Value.Usage).Key;
            _cache.Remove(oldest);
        }
        _cache[query] = new(null, _clock.GetUtcNow(), version, ++_usage);
    }

    private WardrobeCatalogItem? KnownItem(long textureId)
    {
        lock (_cacheGate)
        {
            foreach (var entry in _cache.Values.OrderByDescending(static entry => entry.CreatedUtc))
                if (entry.Page is not null && Fresh(entry.CreatedUtc)
                    && entry.Page.Items.FirstOrDefault(item => item.TextureId == textureId) is { } item)
                    return item;
        }
        return null;
    }

    private static WardrobeCatalogQuery Normalize(WardrobeCatalogQuery query)
    {
        DemandSite(query.SiteId);
        if (query.Page > 1_000_000 || query.Keyword is null || query.Keyword.Length > 256
            || query.Keyword.Any(char.IsControl))
            throw new InvalidDataException("皮肤库筛选条件无效。");
        return query with
        {
            Page = Math.Max(1, query.Page),
            Kind = Enum.IsDefined(query.Kind) ? query.Kind : WardrobeCatalogKind.Skin,
            Order = Enum.IsDefined(query.Order) ? query.Order : WardrobeCatalogOrder.Time,
            Keyword = query.Keyword.Trim(),
            ForceRefresh = false
        };
    }

    private static void DemandSite(string siteId)
    {
        if (!string.Equals(siteId, LittleSkin.Id, StringComparison.Ordinal))
            throw new InvalidDataException("此皮肤站尚未提供皮肤库接口。");
    }

    private static Uri BuildListUri(WardrobeCatalogQuery query)
    {
        string filter = query.Kind == WardrobeCatalogKind.Cape ? "cape" : "skin";
        string sort = query.Order == WardrobeCatalogOrder.Likes ? "likes" : "time";
        string keyword = query.Keyword.Length == 0 ? "" : "&keyword=" + Uri.EscapeDataString(query.Keyword);
        return new(LittleSkin.BaseUri, $"skinlib/list?page={query.Page.ToString(CultureInfo.InvariantCulture)}&filter={filter}&sort={sort}{keyword}");
    }

    private static Uri DetailUri(long id) => new(LittleSkin.BaseUri, "skinlib/info/" + id.ToString(CultureInfo.InvariantCulture));

    private async Task<WardrobeCatalogItem?> ResolvePageItemAsync(WardrobeCatalogItem item, SemaphoreSlim slots,
        CancellationToken token)
    {
        await slots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var document = await ReadJsonAsync(DetailUri(item.TextureId), token).ConfigureAwait(false);
            return ParseDetail(document.RootElement, item.TextureId, item);
        }
        catch (Exception error) when (error is not OperationCanceledException && Recoverable(error)) { return null; }
        finally { slots.Release(); }
    }

    private static CatalogList ParseList(JsonElement root, WardrobeCatalogQuery query)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > MaxPageItems)
            throw new InvalidDataException("皮肤站返回了无效或过大的皮肤库页面。");
        int page = query.Page;
        if (root.TryGetProperty("current_page", out var current))
        {
            if (current.ValueKind != JsonValueKind.Number || !current.TryGetInt32(out page) || page is < 1 or > 1_000_000)
                throw new InvalidDataException("皮肤站返回了无效的页码。");
        }
        List<WardrobeCatalogItem> items = [];
        HashSet<long> ids = [];
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("tid", out var idValue)
                || idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetInt64(out long id) || id <= 0 || !ids.Add(id))
                continue;
            string? model = Model(entry);
            if (model is null) continue;
            var kind = model == "cape" ? WardrobeCatalogKind.Cape : WardrobeCatalogKind.Skin;
            if (kind != query.Kind) continue;
            items.Add(new(id, Text(entry, "name", "Texture " + id.ToString(CultureInfo.InvariantCulture), 256),
                Text(entry, "nickname", "", 128), model, Likes(entry), IsHd(entry), "",
                new(LittleSkin.BaseUri, "skinlib/show/" + id.ToString(CultureInfo.InvariantCulture)), kind));
        }
        return new(page, HasPage(root, "prev_page_url"), HasPage(root, "next_page_url"), items);
    }

    private static WardrobeCatalogItem ParseDetail(JsonElement root, long textureId, WardrobeCatalogItem? known)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("皮肤站返回了无效的纹理详情。");
        if (root.TryGetProperty("tid", out var id) && (id.ValueKind != JsonValueKind.Number
            || !id.TryGetInt64(out long actual) || actual != textureId))
            throw new InvalidDataException("皮肤站返回的纹理不属于此条目。");
        string hash = Text(root, "hash", "", 64);
        if (!root.TryGetProperty("hash", out var hashElement) || hashElement.ValueKind != JsonValueKind.String
            || hashElement.GetString()?.Length != 64 || hash.Length != 64
            || hash.Any(static value => !char.IsAsciiHexDigit(value)))
            throw new InvalidDataException("皮肤站返回了无效的纹理哈希。");
        string? model = root.TryGetProperty("type", out _) ? Model(root) : known?.Model;
        if (model is null) throw new InvalidDataException("皮肤站未提供可验证的纹理类型，请刷新皮肤库。");
        var kind = model == "cape" ? WardrobeCatalogKind.Cape : WardrobeCatalogKind.Skin;
        return new(textureId, Text(root, "name", known?.Name ?? "Texture " + textureId.ToString(CultureInfo.InvariantCulture), 256),
            Text(root, "nickname", known?.Uploader ?? "", 128), model,
            root.TryGetProperty("likes", out _) ? Likes(root) : known?.Likes ?? 0,
            root.TryGetProperty("hd", out _) ? IsHd(root) : known?.IsHighDefinition ?? false,
            new Uri(LittleSkin.BaseUri, "textures/" + hash.ToLowerInvariant()).AbsoluteUri,
            new(LittleSkin.BaseUri, "skinlib/show/" + textureId.ToString(CultureInfo.InvariantCulture)), kind);
    }

    private async Task<SiteIdentity> ReadIdentityAsync(CancellationToken token)
    {
        try
        {
            using var document = await ReadJsonAsync(new(LittleSkin.BaseUri, "api"), token).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new(LittleSkin.DisplayName, "");
            return new(Text(document.RootElement, "site_name", LittleSkin.DisplayName, 256),
                Text(document.RootElement, "blessing_skin", "", 128));
        }
        catch (Exception error) when (error is not OperationCanceledException && Recoverable(error))
        { return new(LittleSkin.DisplayName, ""); }
    }

    private async Task<JsonDocument> ReadJsonAsync(Uri uri, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (http.DefaultRequestHeaders.Contains("Authorization") || http.DefaultRequestHeaders.Contains("Cookie"))
            throw new InvalidDataException("公共皮肤库不能使用账户凭据。");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.UserAgent.ParseAdd("NexaCL/2.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri)
            throw new InvalidDataException("皮肤站将请求重定向到了未授权的地址。");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidDataException("皮肤站响应超过大小限制。");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16_384];
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length,
                MaxResponseBytes - output.Length + 1)), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (read == 0) break;
            if (output.Length + read > MaxResponseBytes) throw new InvalidDataException("皮肤站响应超过大小限制。");
            output.Write(buffer, 0, read);
        }
        token.ThrowIfCancellationRequested();
        return JsonDocument.Parse(output.GetBuffer().AsMemory(0, (int)output.Length), new() { MaxDepth = 32 });
    }

    private static string? Model(JsonElement entry) => Text(entry, "type", "", 16).ToLowerInvariant() switch
    { "steve" or "classic" => "steve", "alex" or "slim" => "alex", "cape" => "cape", _ => null };
    private static int Likes(JsonElement entry) => entry.TryGetProperty("likes", out var likes)
        && likes.ValueKind == JsonValueKind.Number && likes.TryGetInt32(out int number) ? Math.Max(0, number) : 0;
    private static bool IsHd(JsonElement entry) => entry.TryGetProperty("hd", out var hd) && hd.ValueKind == JsonValueKind.True;
    private static bool HasPage(JsonElement root, string name) => root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());
    private static string Text(JsonElement entry, string name, string fallback, int max) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text)
            ? text[..Math.Min(text.Length, max)] : fallback;
    private static bool Recoverable(Exception error) => error is HttpRequestException or IOException
        or InvalidDataException or JsonException or OperationCanceledException;
    private static XsrError Error(Exception error, CancellationToken token) => error switch
    {
        OperationCanceledException when token.IsCancellationRequested => XsrRuntimeErrors.Cancelled(),
        OperationCanceledException => XsrRuntimeErrors.TimedOut(),
        InvalidDataException invalid => new(XsrErrorKind.Rejected, XsrSemanticId.Parse("accounts.wardrobe.catalog.invalid"), invalid.Message),
        _ => new(XsrErrorKind.Unavailable, XsrSemanticId.Parse("accounts.wardrobe.catalog.unavailable"), "皮肤库暂时无法读取，请重试。")
    };

    private sealed record SiteIdentity(string Name, string Version);
    private sealed record CatalogList(int Page, bool HasPrevious, bool HasNext, IReadOnlyList<WardrobeCatalogItem> Items);
    private sealed record CacheEntry(WardrobeCatalogPage? Page, DateTimeOffset CreatedUtc, long Version, long Usage);
}
