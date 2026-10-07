using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Nexa.Services.Accounts;
using Nexa.Xsr;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private const string CatalogHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static async ValueTask WardrobeCatalogMatchesDevSkinAndCapeQueries()
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new CatalogHttp((request, _) =>
        {
            requests.Add(request.RequestUri!);
            AssertTrue(request.Headers.Authorization is null);
            AssertEqual("application/json", request.Headers.Accept.Single().MediaType);
            AssertTrue(request.Headers.GetValues("Accept-Language").Contains("zh-CN"));
            AssertTrue(request.Headers.GetValues("X-Requested-With").Contains("XMLHttpRequest"));
            string body = request.RequestUri!.AbsolutePath switch
            {
                "/api" => """{"site_name":"LittleSkin Test","blessing_skin":"6.0.2"}""",
                "/skinlib/list" when request.RequestUri.Query.Contains("filter=cape", StringComparison.Ordinal) =>
                    """{"current_page":3,"prev_page_url":"ignored","next_page_url":null,"data":[{"tid":84,"name":"Migrator Cape","nickname":"Author","type":"cape","likes":99,"hd":false}]}""",
                "/skinlib/list" => """{"current_page":2,"prev_page_url":"ignored","next_page_url":null,"data":[{"tid":42,"name":"Test Skin","nickname":"Uploader","type":"alex","likes":7,"hd":true}]}""",
                "/skinlib/info/42" => $$"""{"tid":42,"hash":"{{CatalogHash.ToUpperInvariant()}}"}""",
                "/skinlib/info/84" => $$"""{"tid":84,"hash":"{{CatalogHash}}"}""",
                _ => "{}"
            };
            return Task.FromResult(CatalogJson(body));
        }));
        var service = new WardrobeCatalogService(http);
        var sites = await service.SitesAsync(new());
        AssertTrue(sites.IsSuccess); AssertEqual(1, sites.Value.Count);
        AssertEqual("littleskin", sites.Value[0].Id); AssertTrue(sites.Value[0].SupportsCapes);
        AssertEqual("https://manual.littlesk.in/advanced/api", sites.Value[0].DocumentationUri.AbsoluteUri);
        var skin = await service.ReadAsync(new(Page: 2));
        AssertTrue(skin.IsSuccess); AssertEqual("LittleSkin Test", skin.Value.SiteName);
        AssertEqual("6.0.2", skin.Value.ServerVersion); AssertEqual(2, skin.Value.Page);
        AssertTrue(skin.Value.HasPreviousPage); AssertFalse(skin.Value.HasNextPage);
        var item = skin.Value.Items.Single();
        AssertEqual("alex", item.Model); AssertEqual(WardrobeCatalogKind.Skin, item.Kind);
        AssertEqual("Test Skin", item.Name); AssertEqual("Uploader", item.Uploader);
        AssertEqual(7, item.Likes); AssertTrue(item.IsHighDefinition);
        AssertEqual("https://littleskin.cn/textures/" + CatalogHash, item.TextureAddress);
        AssertEqual("https://littleskin.cn/skinlib/show/42", item.DetailsUri.AbsoluteUri);
        var cape = await service.ReadAsync(new(Page: 3, Kind: WardrobeCatalogKind.Cape,
            Order: WardrobeCatalogOrder.Likes, Keyword: "  迁移者 & skin  "));
        AssertTrue(cape.IsSuccess); AssertEqual(WardrobeCatalogKind.Cape, cape.Value.Items.Single().Kind);
        string query = requests.Single(uri => uri.Query.Contains("filter=cape", StringComparison.Ordinal)).Query;
        AssertTrue(query.Contains("page=3&filter=cape&sort=likes", StringComparison.Ordinal));
        AssertTrue(query.Contains("keyword=" + Uri.EscapeDataString("迁移者 & skin"), StringComparison.Ordinal));
        AssertTrue(requests.All(uri => uri.Scheme == "https" && uri.Host == "littleskin.cn"));
    }

    private static async ValueTask WardrobeCatalogCacheExpiresRefreshesAndStaysBounded()
    {
        int calls = 0;
        var clock = new CatalogClock();
        using var http = new HttpClient(new CatalogHttp((request, _) =>
        {
            calls++;
            return Task.FromResult(CatalogJson(request.RequestUri!.AbsolutePath == "/api"
                ? "{}" : """{"data":[]}"""));
        }));
        var service = new WardrobeCatalogService(http, clock);
        AssertTrue((await service.ReadAsync(new())).IsSuccess); AssertEqual(2, calls);
        AssertTrue((await service.ReadAsync(new(Keyword: "  "))).IsSuccess); AssertEqual(2, calls);
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        AssertTrue((await service.ReadAsync(new())).IsSuccess); AssertEqual(2, calls);
        clock.Advance(TimeSpan.FromTicks(1));
        AssertTrue((await service.ReadAsync(new())).IsSuccess); AssertEqual(4, calls);
        AssertTrue((await service.ReadAsync(new(ForceRefresh: true))).IsSuccess); AssertEqual(6, calls);
        AssertTrue((await service.ReadAsync(new())).IsSuccess); AssertEqual(6, calls);
        for (int page = 2; page <= 65; page++) AssertTrue((await service.ReadAsync(new(Page: page))).IsSuccess);
        int afterFill = calls;
        AssertTrue((await service.ReadAsync(new())).IsSuccess); AssertEqual(afterFill + 2, calls);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var canceled = await service.ReadAsync(new(), stop.Token);
        AssertFalse(canceled.IsSuccess); AssertEqual(XsrErrorKind.Cancelled, canceled.Error!.Kind);
        AssertEqual(afterFill + 2, calls);
        foreach (var invalid in new[] { new WardrobeCatalogQuery(SiteId: "https://evil.test"),
            new WardrobeCatalogQuery(Page: 1_000_001), new WardrobeCatalogQuery(Keyword: new string('a', 257)),
            new WardrobeCatalogQuery(Keyword: "control\ncharacter") })
            AssertFalse((await service.ReadAsync(invalid)).IsSuccess);
        AssertEqual(afterFill + 2, calls);
    }

    private static async ValueTask WardrobeCatalogResolveUsesCurrentHashAndAuthoritativeMetadata()
    {
        int version = 1;
        var paths = new List<string>();
        using var http = new HttpClient(new CatalogHttp((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath; paths.Add(path);
            string body = path switch
            {
                "/api" => "{}",
                "/skinlib/list" => """{"data":[{"tid":42,"name":"Known","type":"alex"}]}""",
                "/skinlib/info/42" => $$"""{"tid":42,"hash":"{{(version == 1 ? CatalogHash : new string('a', 64))}}"}""",
                "/skinlib/info/99" => $$"""{"tid":99,"hash":"{{CatalogHash}}","type":"cape","name":"Direct"}""",
                "/skinlib/info/100" => $$"""{"tid":100,"hash":"{{CatalogHash}}"}""",
                "/skinlib/info/101" => $$"""{"tid":42,"hash":"{{CatalogHash}}","type":"steve"}""",
                "/skinlib/info/102" => $$"""{"tid":102,"hash":"{{CatalogHash}}bad","type":"steve"}""",
                _ => "{}"
            };
            return Task.FromResult(CatalogJson(body));
        }));
        var service = new WardrobeCatalogService(http);
        AssertTrue((await service.ReadAsync(new())).IsSuccess);
        version = 2;
        var resolved = await service.ResolveItemAsync("littleskin", 42);
        AssertTrue(resolved.IsSuccess); AssertEqual("Known", resolved.Value.Name);
        AssertEqual("alex", resolved.Value.Model);
        AssertEqual("https://littleskin.cn/textures/" + new string('a', 64), resolved.Value.TextureAddress);
        var direct = await service.ResolveAsync(new("littleskin", 99));
        AssertTrue(direct.IsSuccess); AssertEqual(WardrobeCatalogKind.Cape, direct.Value.Kind);
        AssertFalse((await service.ResolveItemAsync("littleskin", 100)).IsSuccess);
        AssertFalse((await service.ResolveItemAsync("littleskin", 101)).IsSuccess);
        AssertFalse((await service.ResolveItemAsync("littleskin", 102)).IsSuccess);
        int before = paths.Count;
        AssertFalse((await service.ResolveItemAsync("evil", 42)).IsSuccess);
        AssertFalse((await service.ResolveItemAsync("littleskin", -1)).IsSuccess);
        AssertEqual(before, paths.Count);
    }

    private static async ValueTask WardrobeCatalogBoundsBodiesEntriesAndInvalidDetails()
    {
        foreach (int mode in new[] { 0, 1, 2, 3 })
        {
            using var http = new HttpClient(new CatalogHttp((request, _) =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path == "/api") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                { Content = new StringContent("REMOTE-PRIVATE") });
                if (mode == 0)
                {
                    var content = new StreamContent(new CatalogUnknownLengthStream(new byte[1_048_577]));
                    content.Headers.ContentLength = 1;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                }
                string body = mode switch
                {
                    1 => "{\"data\":[" + string.Join(',', Enumerable.Range(1, 65).Select(id =>
                        "{\"tid\":" + id + ",\"type\":\"steve\"}")) + "]}",
                    2 => "[]",
                    _ when path == "/skinlib/list" => """{"data":[{"tid":1,"type":"steve"},{"tid":2,"type":"alex"},{"tid":3,"type":"cape"},{"tid":4,"type":"unknown"}]}""",
                    _ when path == "/skinlib/info/1" => $$"""{"tid":1,"hash":"{{CatalogHash}}"}""",
                    _ => """{"hash":"NOT-A-HASH"}"""
                };
                return Task.FromResult(CatalogJson(body));
            }));
            var result = await new WardrobeCatalogService(http).ReadAsync(new());
            if (mode != 3) AssertFalse(result.IsSuccess);
            else
            {
                AssertTrue(result.IsSuccess); AssertEqual("LittleSkin", result.Value.SiteName);
                AssertEqual("", result.Value.ServerVersion); AssertEqual(1, result.Value.Items.Count);
                AssertEqual(1L, result.Value.Items[0].TextureId);
            }
            AssertFalse(result.IsSuccess ? result.Value.ToString().Contains("REMOTE-PRIVATE", StringComparison.Ordinal)
                : result.Error!.Message.Contains("REMOTE-PRIVATE", StringComparison.Ordinal));
        }
        int authenticatedCalls = 0;
        using var authenticatedHttp = new HttpClient(new CatalogHttp((_, _) =>
        {
            authenticatedCalls++; return Task.FromResult(CatalogJson("{}"));
        }));
        authenticatedHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "PRIVATE");
        AssertFalse((await new WardrobeCatalogService(authenticatedHttp).ReadAsync(new())).IsSuccess);
        AssertEqual(0, authenticatedCalls);
    }

    private static async ValueTask WardrobeCatalogBoundsDetailConcurrencyAndRetiresCancellation()
    {
        int active = 0, maximum = 0, listCalls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool pause = false;
        using var http = new HttpClient(new CatalogHttp(async (request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api") return CatalogJson("{}");
            if (path == "/skinlib/list")
            {
                Interlocked.Increment(ref listCalls);
                return CatalogJson("{\"data\":[" + string.Join(',', Enumerable.Range(1, 16)
                    .Select(id => "{\"tid\":" + id + ",\"type\":\"steve\"}")) + "]}");
            }
            int current = Interlocked.Increment(ref active);
            int observed;
            do { observed = maximum; } while (current > observed && Interlocked.CompareExchange(ref maximum, current, observed) != observed);
            try
            {
                if (pause) { entered.TrySetResult(); await release.Task; }
                else await Task.Delay(5, CancellationToken.None);
                return CatalogJson($$"""{"hash":"{{CatalogHash}}"}""");
            }
            finally { Interlocked.Decrement(ref active); }
        }));
        var service = new WardrobeCatalogService(http);
        AssertTrue((await service.ReadAsync(new())).IsSuccess);
        AssertTrue(maximum is > 1 and <= 4); AssertEqual(0, active);
        pause = true;
        using var stop = new CancellationTokenSource();
        var pending = service.ReadAsync(new(ForceRefresh: true), stop.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel(); release.TrySetResult();
        var canceled = await pending;
        AssertFalse(canceled.IsSuccess); AssertEqual(XsrErrorKind.Cancelled, canceled.Error!.Kind);
        AssertEqual(0, active);
        pause = false;
        AssertTrue((await service.ReadAsync(new())).IsSuccess); AssertEqual(3, listCalls);
    }

    private static async ValueTask WardrobeCatalogOldReadCannotOverwriteExplicitRefresh()
    {
        int listCalls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new CatalogHttp(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api") return CatalogJson("{}");
            if (request.RequestUri.AbsolutePath.StartsWith("/skinlib/info/", StringComparison.Ordinal))
                return CatalogJson($$"""{"hash":"{{CatalogHash}}"}""");
            int current = Interlocked.Increment(ref listCalls);
            if (current == 1) { entered.SetResult(); await release.Task; }
            return CatalogJson("{\"data\":[{\"tid\":42,\"type\":\"alex\",\"name\":\"" + (current == 1 ? "Old" : "New") + "\"}]}");
        }));
        var service = new WardrobeCatalogService(http);
        var old = service.ReadAsync(new()).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var refreshed = await service.ReadAsync(new(ForceRefresh: true));
        AssertTrue(refreshed.IsSuccess); AssertEqual("New", refreshed.Value.Items.Single().Name);
        release.SetResult(); AssertTrue((await old).IsSuccess);
        var cached = await service.ReadAsync(new());
        AssertTrue(cached.IsSuccess); AssertEqual("New", cached.Value.Items.Single().Name); AssertEqual(2, listCalls);
    }

    private static async ValueTask WardrobeCatalogPublicPreviewNeedsNoAccountAndValidatesHdTextures()
    {
        var paths = new List<string>();
        string kind = "alex";
        using var http = new HttpClient(new CatalogHttp((request, _) =>
        {
            AssertTrue(request.Headers.Authorization is null);
            AssertFalse(request.Headers.Contains("Cookie"));
            AssertEqual("https", request.RequestUri!.Scheme);
            AssertEqual("littleskin.cn", request.RequestUri.Host);
            string path = request.RequestUri.AbsolutePath;
            paths.Add(path);
            return Task.FromResult(path == "/skinlib/info/42"
                ? CatalogJson($$"""{"tid":42,"hash":"{{CatalogHash}}","type":"{{kind}}","hd":true}""")
                : new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(WardrobeCreatePng(width: 128, height: kind == "cape" ? 64 : 128)) });
        }));
        // This public catalogue is constructed without an account roster, provider or selected identity.
        var service = new WardrobeCatalogService(http);
        var skin = await service.PreviewAsync(new("littleskin", 42, WardrobeCatalogKind.Skin));
        AssertTrue(skin.IsSuccess); AssertTrue(skin.Value.IsSlim);
        AssertEqual(128, skin.Value.Skin!.Width); AssertEqual(128, skin.Value.Skin.Height);
        AssertEqual("https://littleskin.cn/textures/" + CatalogHash, skin.Value.SkinAddress);
        AssertTrue(skin.Value.Cape is null); AssertTrue(skin.Value.CapeAddress is null);
        kind = "cape";
        var cape = await service.PreviewAsync(new("littleskin", 42, WardrobeCatalogKind.Cape));
        AssertTrue(cape.IsSuccess); AssertFalse(cape.Value.IsSlim);
        AssertEqual(128, cape.Value.Cape!.Width); AssertEqual(64, cape.Value.Cape.Height);
        AssertEqual("https://littleskin.cn/textures/" + CatalogHash, cape.Value.CapeAddress);
        AssertTrue(cape.Value.Skin is null); AssertTrue(cape.Value.SkinAddress is null);
        AssertTrue(paths.SequenceEqual(new[] { "/skinlib/info/42", "/textures/" + CatalogHash,
            "/skinlib/info/42", "/textures/" + CatalogHash }));
        int beforeAuthenticated = paths.Count;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "PRIVATE");
        AssertFalse((await service.PreviewAsync(new("littleskin", 42, WardrobeCatalogKind.Cape))).IsSuccess);
        AssertEqual(beforeAuthenticated, paths.Count);
    }

    private static async ValueTask WardrobeCatalogPreviewRejectsKindMalformedImageAndCancelledCompletion()
    {
        int requests = 0;
        string kind = "cape";
        bool malformed = false, pause = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new CatalogHttp(async (request, _) =>
        {
            Interlocked.Increment(ref requests);
            if (request.RequestUri!.AbsolutePath == "/skinlib/info/42")
                return CatalogJson($$"""{"tid":42,"hash":"{{CatalogHash}}","type":"{{kind}}"}""");
            // Simulate a provider that completes after the caller has retired its preview request.
            if (pause) { entered.TrySetResult(); await release.Task; }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(malformed ? [137, 80, 78, 71] : WardrobeCreatePng()) };
        }));
        var service = new WardrobeCatalogService(http);
        var mismatch = await service.PreviewAsync(new("littleskin", 42, WardrobeCatalogKind.Skin));
        AssertFalse(mismatch.IsSuccess); AssertEqual(XsrErrorKind.Rejected, mismatch.Error!.Kind);
        AssertEqual(1, requests); // A cape's authoritative metadata never reaches the skin image path.
        foreach (var invalid in new[] { new WardrobeCatalogPreviewQuery("https://evil.test", 42, WardrobeCatalogKind.Skin),
            new WardrobeCatalogPreviewQuery("littleskin", 0, WardrobeCatalogKind.Skin),
            new WardrobeCatalogPreviewQuery("littleskin", 42, (WardrobeCatalogKind)999) })
            AssertFalse((await service.PreviewAsync(invalid)).IsSuccess);
        AssertEqual(1, requests);
        kind = "steve"; malformed = true;
        var invalidImage = await service.PreviewAsync(new("littleskin", 42, WardrobeCatalogKind.Skin));
        AssertFalse(invalidImage.IsSuccess); AssertEqual(XsrErrorKind.Rejected, invalidImage.Error!.Kind);
        malformed = false; pause = true;
        using var stop = new CancellationTokenSource();
        var pending = service.PreviewAsync(new("littleskin", 42, WardrobeCatalogKind.Skin), stop.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel(); release.TrySetResult();
        var canceled = await pending;
        AssertFalse(canceled.IsSuccess); AssertEqual(XsrErrorKind.Cancelled, canceled.Error!.Kind);
        pause = false;
        AssertTrue((await service.PreviewAsync(new("littleskin", 42, WardrobeCatalogKind.Skin))).IsSuccess);
    }

    private static HttpResponseMessage CatalogJson(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class CatalogHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
    private sealed class CatalogClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan amount) => _now += amount;
    }
    private sealed class CatalogUnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
