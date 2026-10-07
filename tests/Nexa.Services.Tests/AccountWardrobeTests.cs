using System.Net;
using System.Text;
using Nexa.Services.Accounts;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static AccountService WardrobeAccounts(ILaunchProfilePort? port = null)
    {
        XsrStateStoreBuilder builder = new(); AccountService.DeclareState(builder); AccountSkinService.DeclareState(builder);
        var accounts = new AccountService(builder.Build(), port ?? new ThrowingProfilePort());
        accounts.ConfigureRegionalPolicy(new("CN")); return accounts;
    }
    private static LaunchProfile WardrobeMicrosoft(string name = "Alice") => new()
    {
        Username = name,
        Kind = LaunchProfileKind.Microsoft,
        Uuid = name == "Alice" ? "0123456789abcdef0123456789abcdef" : "1123456789abcdef0123456789abcdef",
        AccessToken = "PRIVATE-" + name,
        SkinAddress = "https://textures.minecraft.net/texture/original"
    };
    private static async ValueTask WardrobeMicrosoftUsesValidatedBytesAndOwnedCapes()
    {
        var accounts = WardrobeAccounts(); AssertTrue(accounts.AddProfile(WardrobeMicrosoft()).IsSuccess);
        using var handler = new WardrobeHttp(); using var http = new HttpClient(handler);
        using var skins = new AccountSkinService(accounts, http);
        var service = new AccountWardrobeService(accounts, http, skins);
        var read = await service.ReadAsync(new()); AssertTrue(read.IsSuccess); AssertEqual(2, read.Value.Capes.Count);
        AssertTrue(read.Value.Capes[0].IsActive);
        AssertTrue((await service.SetCapeAsync(new(read.Value.Identity, "owned-2"))).IsSuccess);
        AssertTrue(handler.Requests.Any(r => r.Method == "PUT" && Encoding.UTF8.GetString(r.Body).Contains("owned-2", StringComparison.Ordinal)));
        AssertFalse((await service.SetCapeAsync(new(read.Value.Identity, "not-owned"))).IsSuccess);
        AssertEqual(1, handler.Requests.Count(r => r.Method == "PUT"));
        AssertTrue((await service.SetCapeAsync(new(read.Value.Identity, null))).IsSuccess);
        AssertEqual(1, handler.Requests.Count(r => r.Method == "DELETE"));
        byte[] png = SkinFixture();
        AssertTrue((await service.UploadSkinAsync(new(read.Value.Identity, png, "chosen.png", false))).IsSuccess);
        await skins.WhenIdle;
        var upload = handler.Requests.Single(r => r.Method == "POST");
        AssertTrue(upload.Body.AsSpan().IndexOf(png) >= 0);
        AssertTrue(Encoding.UTF8.GetString(upload.Body).Contains("classic", StringComparison.Ordinal));
        AssertEqual("https://textures.minecraft.net/texture/updated", accounts.GetProfile(0).Value.SkinAddress);
        AssertTrue(handler.Requests.Where(r => r.Uri.Host == "api.minecraftservices.com").All(r => r.Token == "PRIVATE-Alice"));
        AssertTrue(handler.Requests.Where(r => r.Uri.Host == "textures.minecraft.net").All(r => r.Token is null));
        AssertFalse(read.Value.ToString().Contains("PRIVATE-Alice", StringComparison.Ordinal));
    }
    private static async ValueTask WardrobeRejectsUnsupportedInvalidAndStaleRequests()
    {
        var accounts = WardrobeAccounts(); AssertTrue(accounts.AddProfile(SampleProfile()).IsSuccess);
        using var handler = new WardrobeHttp(); using var http = new HttpClient(handler);
        using var skins = new AccountSkinService(accounts, http);
        var service = new AccountWardrobeService(accounts, http, skins);
        var offline = await service.ReadAsync(new()); AssertTrue(offline.IsSuccess);
        AssertFalse(offline.Value.CanUploadSkin); AssertFalse(offline.Value.CanChooseCape);
        AssertFalse((await service.UploadSkinAsync(new(offline.Value.Identity, SkinFixture(), "skin.png", false))).IsSuccess);
        AssertEqual(0, handler.Requests.Count);
        AssertTrue(accounts.ReplaceProfile(0, WardrobeMicrosoft()).IsSuccess);
        var active = await service.ReadAsync(new()); AssertTrue(active.IsSuccess);
        int before = handler.Requests.Count;
        AssertFalse((await service.UploadSkinAsync(new(active.Value.Identity, [1, 2, 3], "skin.png", false))).IsSuccess);
        AssertEqual(before, handler.Requests.Count);
        foreach (string mutation in new[] { "selection_aba", "remove_readd", "replace", "unrelated" })
        {
            var stamp = (await service.ReadAsync(new())).Value.Identity;
            var original = accounts.GetProfile(0).Value;
            if (mutation == "selection_aba")
            {
                AssertTrue(accounts.AddProfile(WardrobeMicrosoft("Bob")).IsSuccess);
                AssertTrue(accounts.SelectProfile(1) is null); AssertTrue(accounts.SelectProfile(0) is null);
            }
            else if (mutation == "remove_readd")
            {
                while (accounts.GetViews().Count > 0) AssertTrue(accounts.RemoveProfile(0).IsSuccess);
                AssertTrue(accounts.AddProfile(original).IsSuccess);
            }
            else if (mutation == "replace") AssertTrue(accounts.ReplaceProfile(0, original with { AccessToken = "REPLACED" }).IsSuccess);
            else AssertTrue(accounts.AddProfile(WardrobeMicrosoft("Bob")).IsSuccess);
            before = handler.Requests.Count;
            AssertFalse((await service.SetCapeAsync(new(stamp, null))).IsSuccess);
            AssertFalse((await service.UploadSkinAsync(new(stamp, SkinFixture(), "skin.png", false))).IsSuccess);
            AssertEqual(before, handler.Requests.Count);
        }
    }
    private static async ValueTask WardrobeLateResponsesAndFailuresPreserveCurrentAccount()
    {
        foreach (string mutation in new[] { "select", "remove_readd", "replace", "cancel", "persist" })
        {
            var port = new ThrowingProfilePort(); var accounts = WardrobeAccounts(port);
            var original = WardrobeMicrosoft(); AssertTrue(accounts.AddProfile(original).IsSuccess);
            AssertTrue(accounts.AddProfile(WardrobeMicrosoft("Bob")).IsSuccess);
            using var handler = new WardrobeHttp { PauseUpload = true }; using var http = new HttpClient(handler);
            using var skins = new AccountSkinService(accounts, http);
            var service = new AccountWardrobeService(accounts, http, skins);
            var stamp = (await service.ReadAsync(new())).Value.Identity;
            using var stop = new CancellationTokenSource();
            var pending = service.UploadSkinAsync(new(stamp, SkinFixture(), "skin.png", false), stop.Token).AsTask();
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (mutation == "select") AssertTrue(accounts.SelectProfile(1) is null);
            if (mutation == "remove_readd")
            {
                AssertTrue(accounts.RemoveProfile(0).IsSuccess); AssertTrue(accounts.RemoveProfile(0).IsSuccess);
                AssertTrue(accounts.AddProfile(original).IsSuccess);
            }
            if (mutation == "replace") AssertTrue(accounts.ReplaceProfile(0, original with { AccessToken = "NEW" }).IsSuccess);
            if (mutation == "cancel") stop.Cancel();
            if (mutation == "persist") port.SaveShouldThrow = true;
            var before = accounts.GetViews().ToArray();
            handler.UploadDone.SetResult();
            AssertFalse((await pending).IsSuccess);
            AssertTrue(before.SequenceEqual(accounts.GetViews()));
            AssertEqual("https://textures.minecraft.net/texture/original", accounts.GetProfile(0).Value.SkinAddress);
        }
        var failureAccounts = WardrobeAccounts(); failureAccounts.AddProfile(WardrobeMicrosoft());
        using var failureHandler = new WardrobeHttp { RejectAll = true }; using var failureHttp = new HttpClient(failureHandler);
        using var failureSkins = new AccountSkinService(failureAccounts, failureHttp);
        var failed = await new AccountWardrobeService(failureAccounts, failureHttp, failureSkins).ReadAsync(new());
        AssertTrue(failed.IsSuccess);
        AssertEqual(AccountWardrobeCapeState.LoadFailed, failed.Value.CapeState);
        AssertFalse(failed.Value.ToString().Contains("REMOTE-PRIVATE-TOKEN", StringComparison.Ordinal));
    }
    private static async ValueTask WardrobeLittleSkinUsesProviderAndGameTokensAndClearsCape()
    {
        var accounts = WardrobeAccounts(); accounts.AddProfile(new LaunchProfile
        {
            Username = "Alice",
            Kind = LaunchProfileKind.LittleSkin,
            Uuid = "0123456789abcdef0123456789abcdef",
            AccessToken = "OLD-GAME",
            ProviderAccessToken = "PROVIDER",
            ProviderTokenExpiresAtUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        });
        using var handler = new WardrobeHttp { RenameLittleSkin = true }; using var http = new HttpClient(handler);
        using var skins = new AccountSkinService(accounts, http);
        var service = new AccountWardrobeService(accounts, http, skins, littleSkin: new LittleSkinOAuthService(http));
        var read = await service.ReadAsync(new()); AssertTrue(read.IsSuccess); AssertEqual("22", read.Value.Capes.Single().Id);
        AssertEqual("Renamed", read.Value.Profile.Username);
        AssertTrue((await service.SetCapeAsync(new(read.Value.Identity, null))).IsSuccess);
        AssertTrue(handler.Requests.Any(r => r.Uri.AbsolutePath == "/api/players/7/textures"
            && r.Token == "PROVIDER" && Encoding.UTF8.GetString(r.Body) == "cape=0"));
        read = await service.ReadAsync(new()); AssertTrue(read.IsSuccess);
        AssertTrue((await service.UploadSkinAsync(new(read.Value.Identity, SkinFixture(), "skin.png", false))).IsSuccess);
        await skins.WhenIdle;
        AssertTrue(handler.Requests.Any(r => r.Uri.AbsolutePath.EndsWith("/skin", StringComparison.Ordinal) && r.Token == "GAME"));
    }
    private static async ValueTask WardrobeBoundsChunkedResponsesAndCapeInventories()
    {
        foreach (bool chunked in new[] { false, true })
        {
            var accounts = WardrobeAccounts(); accounts.AddProfile(WardrobeMicrosoft());
            using var handler = new WardrobeHttp { OversizedProfile = chunked, ExcessiveCapes = !chunked };
            using var http = new HttpClient(handler); using var skins = new AccountSkinService(accounts, http);
            var result = await new AccountWardrobeService(accounts, http, skins).ReadAsync(new());
            AssertTrue(result.IsSuccess);
            AssertEqual(AccountWardrobeCapeState.LoadFailed, result.Value.CapeState);
            AssertEqual(0, result.Value.Capes.Count);
            AssertEqual(1, handler.Requests.Count(request => request.Uri.AbsolutePath == "/minecraft/profile"));
        }
    }
    private sealed class WardrobeHttp : HttpMessageHandler
    {
        internal readonly List<(string Method, Uri Uri, string? Token, byte[] Body)> Requests = [];
        internal bool PauseUpload, RejectAll, RenameLittleSkin, OversizedProfile, ExcessiveCapes;
        internal long LittleSkinSkinId = 11, LittleSkinCapeId = 22;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource UploadDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            lock (Requests) Requests.Add((request.Method.Method, request.RequestUri!, request.Headers.Authorization?.Parameter, body));
            if (RejectAll) return new(HttpStatusCode.Unauthorized) { Content = new StringContent("REMOTE-PRIVATE-TOKEN") };
            if (request.Method == HttpMethod.Post && request.RequestUri!.Host == "api.minecraftservices.com" && PauseUpload)
            { Entered.TrySetResult(); await UploadDone.Task; } // Deliberately ignores cancellation to exercise admission.
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/players/7/textures")
            {
                string form = Encoding.UTF8.GetString(body);
                if (form.StartsWith("cape=", StringComparison.Ordinal)) LittleSkinCapeId = long.Parse(form[5..], System.Globalization.CultureInfo.InvariantCulture);
                if (form.StartsWith("skin=", StringComparison.Ordinal)) LittleSkinSkinId = long.Parse(form[5..], System.Globalization.CultureInfo.InvariantCulture);
            }
            if (path == "/minecraft/profile" && OversizedProfile)
                return new(HttpStatusCode.OK) { Content = new StreamContent(new UnseekableWardrobeStream(new byte[1_048_577])) };
            if (path == "/minecraft/profile" && ExcessiveCapes)
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"capes\":[" + string.Join(',',
                    Enumerable.Repeat("{\"id\":\"same\",\"url\":\"https://textures.minecraft.net/cape\"}", 257)) + "]}")
                };
            string json = path switch
            {
                "/minecraft/profile" => """{"capes":[{"id":"owned-1","alias":"First","url":"https://textures.minecraft.net/1","state":"ACTIVE"},{"id":"owned-2","alias":"Second","url":"https://textures.minecraft.net/2","state":"INACTIVE"}]}""",
                "/minecraft/profile/skins" => """{"skins":[{"url":"https://textures.minecraft.net/texture/updated","state":"ACTIVE"}]}""",
                "/api/yggdrasil/authserver/oauth" => """{"accessToken":"GAME","clientToken":"CLIENT","selectedProfile":{"id":"0123456789abcdef0123456789abcdef","name":"Alice"}}""",
                "/api/players" => $$"""[{"pid":7,"name":"Alice","tid_skin":{{LittleSkinSkinId}},"tid_cape":{{LittleSkinCapeId}}}]""",
                "/api/closet" when request.RequestUri.Query.Contains("category=skin", StringComparison.Ordinal) =>
                    """{"last_page":1,"data":[{"tid":11,"hash":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB","type":"alex","pivot":{"item_name":"Skin"}}]}""",
                "/api/closet" => """{"last_page":1,"data":[{"tid":22,"hash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","type":"cape","pivot":{"item_name":"Cape"}}]}""",
                "/api/players/7/textures" => """{"code":0,"message":"ok"}""",
                _ => "{}"
            };
            if (RenameLittleSkin && path is "/api/yggdrasil/authserver/oauth" or "/api/players")
                json = json.Replace("Alice", "Renamed", StringComparison.Ordinal);
            if (request.RequestUri.Host == "textures.minecraft.net") return new(HttpStatusCode.OK) { Content = new ByteArrayContent(SkinFixture()) };
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
    private sealed class UnseekableWardrobeStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
