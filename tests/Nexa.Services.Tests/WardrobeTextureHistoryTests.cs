using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Nexa.Services.Accounts;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static LaunchProfileView WardrobeTextureProfile(string? address = null, LaunchProfileKind kind = LaunchProfileKind.Microsoft,
        string uuid = "12345678-1234-1234-1234-123456789abc", string authServer = "")
        => new(0, "Alice", "", kind, uuid, "", "lucide/user", address, authServer);

    private static async ValueTask WardrobeTextureResolvesSkinCapeModelAndLegacyMetadata()
    {
        byte[] skin = WardrobeCreatePng(width: 128, height: 128), cape = WardrobeCreatePng(width: 128, height: 64);
        string textures = "{\"textures\":{\"SKIN\":{\"url\":\"http://textures.minecraft.net/skin\",\"metadata\":{\"model\":\"slim\"}},\"CAPE\":{\"url\":\"https://textures.minecraft.net/cape\"}}}";
        string metadata = "{\"id\":\"12345678123412341234123456789abc\",\"properties\":[{\"name\":\"textures\",\"value\":\""
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(textures)) + "\"}]}";
        bool littleSkinEndpointSeen = false;
        using var handler = new WardrobeTextureHttp(request =>
        {
            littleSkinEndpointSeen |= request.RequestUri!.AbsoluteUri.StartsWith(LittleSkinOAuthService.YggdrasilServer, StringComparison.Ordinal);
            return request.RequestUri.AbsolutePath switch
            {
                "/skin" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(skin) },
                "/cape" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(cape) },
                _ => new(HttpStatusCode.OK) { Content = new StringContent(metadata, Encoding.UTF8, "application/json") },
            };
        });
        using var http = new HttpClient(handler); var resolver = new WardrobeTextureResolver(http);
        var result = await resolver.ResolveAsync(WardrobeTextureProfile());
        AssertEqual("https://textures.minecraft.net/skin", result.SkinAddress!);
        AssertEqual("https://textures.minecraft.net/cape", result.CapeAddress!);
        AssertTrue(result.IsSlim); AssertEqual(128, result.Skin!.Width); AssertEqual(64, result.Cape!.Height);
        AssertFalse(handler.CredentialsSeen); AssertEqual(3, handler.Requests);
        var references = await resolver.ResolveReferencesAsync(WardrobeTextureProfile());
        AssertEqual(result.SkinAddress!, references.SkinAddress!); AssertEqual(result.CapeAddress!, references.CapeAddress!);
        AssertTrue(references.IsSlim); AssertTrue(references.Skin is null && references.Cape is null);
        AssertEqual(4, handler.Requests); // Metadata only; encoded textures stay lazy for other profiles.
        var littleSkin = await resolver.ResolveReferencesAsync(WardrobeTextureProfile(kind: LaunchProfileKind.LittleSkin));
        AssertTrue(littleSkinEndpointSeen); AssertEqual(result.CapeAddress!, littleSkin.CapeAddress!); AssertTrue(littleSkin.IsSlim);

        // A legacy public metadata address resolves once without requiring a session UUID.
        var legacy = await resolver.ResolveAsync(WardrobeTextureProfile("https://profiles.example.test/public", uuid: ""));
        AssertEqual(result.SkinAddress!, legacy.SkinAddress!); AssertTrue(legacy.IsSlim); AssertTrue(legacy.Cape is not null);
        var classicOffline = await resolver.ResolveAsync(WardrobeTextureProfile(kind: LaunchProfileKind.Offline, uuid: "0000000000000000000000000000000a"));
        var slimOffline = await resolver.ResolveAsync(WardrobeTextureProfile(kind: LaunchProfileKind.Offline, uuid: "0000000000000000000000000000000b"));
        AssertFalse(classicOffline.IsSlim); AssertTrue(slimOffline.IsSlim);
    }

    private static async ValueTask WardrobeTextureFallsBackBoundsInputAndCancels()
    {
        string[] badAddresses = ["http://untrusted.example.test/skin", "https://user:secret@example.test/skin", "https://example.test/skin?token=secret",
            "https://localhost/skin", "https://127.0.0.1/skin", "https://[::1]/skin", "file:///tmp/skin.png", "https://example.test:8443/skin",
            "http://textures.minecraft.net:8443/skin"];
        using var failHandler = new WardrobeTextureHttp(_ => new(HttpStatusCode.BadGateway));
        using var failHttp = new HttpClient(failHandler); var failResolver = new WardrobeTextureResolver(failHttp);
        foreach (string address in badAddresses) AssertTrue(await failResolver.ReadImageAsync(address, AccountWardrobeTextureKind.Skin) is null);
        AssertEqual(0, failHandler.Requests);
        var fallback = await failResolver.ResolveAsync(WardrobeTextureProfile("https://textures.minecraft.net/known"));
        AssertEqual("https://textures.minecraft.net/known", fallback.SkinAddress!); AssertTrue(fallback.Skin is null && fallback.Cape is null);

        foreach (byte[] bytes in new[] { new byte[1_048_577], WardrobeCreatePng()[..^1], WardrobeCreatePng(width: 1024, height: 1024) })
        {
            using var handler = new WardrobeTextureHttp(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new UnseekableWardrobeStream(bytes)) });
            using var http = new HttpClient(handler);
            AssertTrue(await new WardrobeTextureResolver(http).ReadImageAsync("https://textures.minecraft.net/skin", AccountWardrobeTextureKind.Skin) is null);
        }
        using var metadataHandler = new WardrobeTextureHttp(_ => new(HttpStatusCode.OK)
        { Content = new StreamContent(new UnseekableWardrobeStream(new byte[128 * 1024 + 1])) });
        using var metadataHttp = new HttpClient(metadataHandler);
        AssertTrue((await new WardrobeTextureResolver(metadataHttp).ResolveAsync(WardrobeTextureProfile())).Skin is null);

        string mismatchedTexture = "{\"profileId\":\"00000000000000000000000000000001\",\"textures\":{\"SKIN\":{\"url\":\"https://textures.minecraft.net/wrong\"}}}";
        string mismatchedProfile = "{\"properties\":[{\"name\":\"textures\",\"value\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(mismatchedTexture)) + "\"}]}";
        using var mismatchHandler = new WardrobeTextureHttp(_ => new(HttpStatusCode.OK) { Content = new StringContent(mismatchedProfile) });
        using var mismatchHttp = new HttpClient(mismatchHandler);
        var mismatch = await new WardrobeTextureResolver(mismatchHttp).ResolveReferencesAsync(WardrobeTextureProfile("https://textures.minecraft.net/known"));
        AssertEqual("https://textures.minecraft.net/known", mismatch.SkinAddress!); AssertTrue(mismatch.CapeAddress is null);

        // Public image requests never inherit a caller's account bearer token.
        failHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "do-not-send");
        int before = failHandler.Requests;
        AssertTrue(await failResolver.ReadImageAsync("https://textures.minecraft.net/skin", AccountWardrobeTextureKind.Skin) is null);
        AssertEqual(before, failHandler.Requests);

        using var pausedHandler = new WardrobeTextureHttp(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(SkinFixture()) }, pause: true);
        using var pausedHttp = new HttpClient(pausedHandler); using var stop = new CancellationTokenSource();
        Task pending = new WardrobeTextureResolver(pausedHttp).ReadImageAsync("https://textures.minecraft.net/skin", AccountWardrobeTextureKind.Skin,
            cancellationToken: stop.Token).AsTask();
        await pausedHandler.Entered.Task; stop.Cancel();
        bool cancelled = false;
        try { await pending; } catch (OperationCanceledException) { cancelled = true; }
        AssertTrue(cancelled);
    }

    private static async ValueTask WardrobeTextureBoundsProfilesAndParallelReads()
    {
        using var handler = new WardrobeTextureHttp(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(SkinFixture()) }, delay: true);
        using var http = new HttpClient(handler); var resolver = new WardrobeTextureResolver(http);
        LaunchProfileView[] profiles = Enumerable.Range(0, 100)
            .Select(index => WardrobeTextureProfile("https://textures.minecraft.net/skin" + index, LaunchProfileKind.Offline)).ToArray();
        var result = await resolver.ResolveAllAsync(profiles);
        AssertEqual(64, result.Count); AssertEqual(64, handler.Requests); AssertTrue(handler.MaximumConcurrent <= 3);
        AssertTrue(result.All(texture => texture.Skin is not null));
    }

    private static void WardrobeTextureValidatesHdPreviewsWithoutRelaxingUploads()
    {
        foreach (int width in new[] { 64, 128, 192, 512 })
        {
            AssertEqual(width, WardrobeSkinValidator.ValidateTexture(WardrobeCreatePng(width: width, height: width), AccountWardrobeTextureKind.Skin, true).Width);
            AssertEqual(width / 2, WardrobeSkinValidator.ValidateTexture(WardrobeCreatePng(width: width, height: width / 2), AccountWardrobeTextureKind.Cape).Height);
            AssertEqual(width / 2, WardrobeSkinValidator.ValidateTexture(WardrobeCreatePng(width: width, height: width / 2), AccountWardrobeTextureKind.Skin).Height);
        }
        WardrobeAssertInvalid(WardrobeCreatePng(width: 128, height: 128));
        foreach ((int width, int height, AccountWardrobeTextureKind kind, bool slim) in new[]
        {
            (32, 16, AccountWardrobeTextureKind.Cape, false), (64, 64, AccountWardrobeTextureKind.Cape, false),
            (512, 513, AccountWardrobeTextureKind.Skin, false), (576, 576, AccountWardrobeTextureKind.Skin, false),
            (128, 64, AccountWardrobeTextureKind.Skin, true),
        })
        {
            bool invalid = false;
            try { WardrobeSkinValidator.ValidateTexture(WardrobeCreatePng(width: width, height: height), kind, slim); }
            catch (InvalidDataException) { invalid = true; }
            AssertTrue(invalid);
        }
    }

    private static async ValueTask WardrobeHistoryPreservesDevSchemaMruAndOptionalFailure()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexa-wardrobe-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var history = new WardrobeHistoryStore(directory);
            DateTimeOffset latest = new(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(8));
            AccountWardrobeHistoryEntry[] entries = Enumerable.Range(0, 100).Select(index => new AccountWardrobeHistoryEntry("Microsoft:uuid" + index,
                "Alice", AccountWardrobeTextureKind.Skin, "https://textures.minecraft.net/skin" + index, false, latest.AddMinutes(-index))).ToArray();
            await history.RecordAsync(entries);
            await history.RecordAsync([new("Microsoft:new", "New", AccountWardrobeTextureKind.Skin, "https://textures.minecraft.net/SKIN0", true, latest.AddMinutes(1)),
                new("Microsoft:new", "Cape", AccountWardrobeTextureKind.Cape, "https://textures.minecraft.net/skin0", false, latest.AddMinutes(2)),
                new("Microsoft:bad", "Bad", AccountWardrobeTextureKind.Skin, "https://example.test/skin?token=secret", false, latest.AddMinutes(3))]);
            var loaded = await new WardrobeHistoryStore(directory).LoadAsync();
            AssertEqual(80, loaded.Count); AssertEqual(AccountWardrobeTextureKind.Cape, loaded[0].Kind);
            AssertEqual("New", loaded[1].DisplayName); AssertTrue(loaded[1].IsSlim); AssertEqual(TimeSpan.Zero, loaded[0].LastUsedUtc.Offset);
            AssertEqual(1, loaded.Count(entry => entry.Kind == AccountWardrobeTextureKind.Skin && entry.Address.EndsWith("skin0", StringComparison.OrdinalIgnoreCase)));
            string path = Path.Combine(directory, "Appearance", "history.json");
            using (JsonDocument raw = JsonDocument.Parse(await File.ReadAllBytesAsync(path)))
            {
                JsonElement entry = raw.RootElement[0];
                foreach (string name in new[] { "ProfileKey", "DisplayName", "Kind", "Address", "IsSlim", "LastUsedUtc" }) AssertTrue(entry.TryGetProperty(name, out _));
                AssertEqual(JsonValueKind.Number, entry.GetProperty("Kind").ValueKind);
            }
            AssertFalse(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any());
            AssertEqual(WardrobeHistoryStore.ProfileKey(WardrobeTextureProfile()),
                WardrobeHistoryStore.ProfileKey(WardrobeTextureProfile("https://textures.minecraft.net/new") with { Username = "Renamed" }));

            await File.WriteAllTextAsync(path, "invalid json");
            AssertEqual(0, (await new WardrobeHistoryStore(directory).LoadAsync()).Count);
            AssertEqual(80, (await history.LoadAsync()).Count);
            await File.WriteAllBytesAsync(path, new byte[512 * 1024 + 1]);
            AssertEqual(0, (await new WardrobeHistoryStore(directory).LoadAsync()).Count);

            string blockedDirectory = Path.Combine(directory, "blocked"); await File.WriteAllTextAsync(blockedDirectory, "file, not directory");
            var optional = new WardrobeHistoryStore(blockedDirectory);
            await optional.RecordAsync([entries[0]]); AssertEqual(1, (await optional.LoadAsync()).Count);
            var memory = new WardrobeHistoryStore(); await memory.RecordAsync([entries[0]]); AssertEqual(1, (await memory.LoadAsync()).Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class WardrobeTextureHttp(Func<HttpRequestMessage, HttpResponseMessage> reply, bool pause = false, bool delay = false) : HttpMessageHandler
    {
        private int _requests, _concurrent, _maximum;
        internal int Requests => _requests;
        internal int MaximumConcurrent => _maximum;
        internal bool CredentialsSeen { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            int concurrent = Interlocked.Increment(ref _concurrent);
            int maximum;
            do { maximum = _maximum; } while (concurrent > maximum && Interlocked.CompareExchange(ref _maximum, concurrent, maximum) != maximum);
            try
            {
                CredentialsSeen |= request.Headers.Authorization is not null || request.Headers.Contains("Cookie");
                Entered.TrySetResult();
                if (pause) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                if (delay) await Task.Delay(5, cancellationToken);
                return reply(request);
            }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
    }
}
