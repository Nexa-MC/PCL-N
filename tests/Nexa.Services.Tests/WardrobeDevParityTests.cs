using System.Net;
using System.Text;
using Nexa.Services.Accounts;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static LaunchProfile ParityLittleSkin() => WardrobeMicrosoft() with
    {
        Kind = LaunchProfileKind.LittleSkin,
        AccessToken = "OLD-GAME",
        ProviderAccessToken = "PROVIDER",
        ProviderTokenExpiresAtUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
    };

    private static async ValueTask WardrobeProjectsMruOtherProfilesAndOwnedCapePrecedence()
    {
        var accounts = WardrobeAccounts(); AssertTrue(accounts.AddProfile(WardrobeMicrosoft()).IsSuccess);
        AssertTrue(accounts.AddProfile(WardrobeMicrosoft("Bob")).IsSuccess);
        var history = new WardrobeHistoryStore();
        string key = WardrobeHistoryStore.ProfileKey(accounts.GetViews()[0]);
        var now = DateTimeOffset.UtcNow;
        await history.RecordAsync([
            new(key, "Older", AccountWardrobeTextureKind.Skin, "https://textures.minecraft.net/older", false, now.AddMinutes(-2)),
            new(key, "Recent", AccountWardrobeTextureKind.Skin, "https://textures.minecraft.net/recent", true, now.AddMinutes(-1)),
            new("Microsoft:other", "Historical cape", AccountWardrobeTextureKind.Cape,
                "https://textures.minecraft.net/history-cape", false, now)
        ]);
        using var handler = new ParityWardrobeHttp { SessionTextures = true };
        using var http = new HttpClient(handler); using var skins = new AccountSkinService(accounts, http);
        var service = new AccountWardrobeService(accounts, http, skins, history);
        var result = await service.ReadAsync(new()); AssertTrue(result.IsSuccess);
        var snapshot = result.Value;
        AssertEqual("https://textures.minecraft.net/alice-skin", snapshot.Current!.SkinAddress!);
        AssertTrue(snapshot.Current.IsSlim); AssertTrue(snapshot.Current.Skin is not null);
        AssertEqual("https://textures.minecraft.net/owned-cape", snapshot.Current.CapeAddress!);
        AssertEqual(32, snapshot.Current.Cape!.Height);
        AssertEqual(3, snapshot.Skins.Count);
        AssertEqual("Bob", snapshot.Skins[0].Title); AssertEqual("其他档案", snapshot.Skins[0].Source);
        AssertEqual("Recent", snapshot.Skins[1].Title); AssertTrue(snapshot.Skins[1].Appearance.IsSlim);
        AssertEqual("此前使用", snapshot.Skins[1].Source); AssertEqual("Older", snapshot.Skins[2].Title);
        AssertTrue(snapshot.Skins.All(card => card.CanApply && card.Kind == AccountWardrobeTextureKind.Skin));
        AssertEqual(1, snapshot.CapeCards.Count);
        AssertTrue(snapshot.CapeCards[0].IsActive); AssertFalse(snapshot.CapeCards[0].CanApply);
        AssertFalse(snapshot.CapeCards.Any(card => card.Title == "Historical cape"));
        AssertTrue(handler.Requests.Where(request => request.Uri.Host != "api.minecraftservices.com").All(request => request.Token is null));
    }

    private static async ValueTask WardrobeMicrosoftQuietRefreshFailurePreservesPreviewAndOwnershipTruth()
    {
        foreach (bool timeout in new[] { false, true })
        {
            var accounts = WardrobeAccounts(); accounts.AddProfile(WardrobeMicrosoft() with { RefreshToken = "REFRESH" });
            using var handler = new ParityWardrobeHttp { SessionTextures = true };
            using var http = new HttpClient(handler); using var skins = new AccountSkinService(accounts, http);
            var auth = new ParityMicrosoftRefresh { Fail = true, Timeout = timeout };
            var service = new AccountWardrobeService(accounts, http, skins, microsoft: auth, microsoftClientId: "client");
            var read = await service.ReadAsync(new()); AssertTrue(read.IsSuccess);
            AssertEqual(1, auth.Calls); AssertEqual(AccountWardrobeCapeState.Loaded, read.Value.CapeState);
            AssertEqual(1, read.Value.Capes.Count); AssertTrue(read.Value.ProviderStatus is not null);
            AssertTrue(read.Value.Current!.Skin is not null);
            AssertEqual("PRIVATE-Alice", handler.Requests.Single(request => request.Uri.AbsolutePath == "/minecraft/profile").Token!);
            AssertFalse(read.Value.ToString().Contains("PRIVATE", StringComparison.Ordinal));
            AssertEqual("PRIVATE-Alice", accounts.GetProfile(0).Value.AccessToken);
        }
        foreach (bool failure in new[] { false, true })
        {
            var accounts = WardrobeAccounts(); accounts.AddProfile(WardrobeMicrosoft());
            var history = new WardrobeHistoryStore();
            await history.RecordAsync([new("other", "Not owned", AccountWardrobeTextureKind.Cape,
                "https://textures.minecraft.net/history-cape", false, DateTimeOffset.UtcNow)]);
            using var handler = new ParityWardrobeHttp { EmptyCapes = !failure, FailCapes = failure, SessionTextures = true };
            using var http = new HttpClient(handler); using var skins = new AccountSkinService(accounts, http);
            var read = await new AccountWardrobeService(accounts, http, skins, history).ReadAsync(new());
            AssertTrue(read.IsSuccess); AssertEqual(0, read.Value.Capes.Count); AssertEqual(0, read.Value.CapeCards.Count);
            AssertEqual(failure ? AccountWardrobeCapeState.LoadFailed : AccountWardrobeCapeState.Loaded, read.Value.CapeState);
            AssertEqual(failure, read.Value.ProviderStatus is not null);
            AssertEqual("https://textures.minecraft.net/session-cape", read.Value.Current!.CapeAddress!);
            AssertFalse(read.Value.ToString().Contains("REMOTE-PRIVATE", StringComparison.Ordinal));
        }
    }

    private static async ValueTask WardrobeMicrosoftOwnershipUnauthorizedRefreshesOnceAndRetiresOldStamp()
    {
        var accounts = WardrobeAccounts(); accounts.AddProfile(WardrobeMicrosoft() with { RefreshToken = "REFRESH" });
        using var handler = new ParityWardrobeHttp { RejectFirstCapeRead = true };
        using var http = new HttpClient(handler); using var skins = new AccountSkinService(accounts, http);
        var auth = new ParityMicrosoftRefresh();
        var service = new AccountWardrobeService(accounts, http, skins, microsoft: auth, microsoftClientId: "client");
        var read = await service.ReadAsync(new()); AssertTrue(read.IsSuccess);
        AssertEqual(2, auth.Calls); AssertEqual(AccountWardrobeCapeState.Loaded, read.Value.CapeState);
        var requests = handler.Requests.Where(request => request.Uri.AbsolutePath == "/minecraft/profile").ToArray();
        AssertEqual(2, requests.Length); AssertEqual("ROTATED-1", requests[0].Token!); AssertEqual("ROTATED-2", requests[1].Token!);
        AssertEqual("ROTATED-2", accounts.GetProfile(0).Value.AccessToken);
        AssertFalse((await service.SetCapeAsync(new(read.Value.Identity with
        { RosterGeneration = read.Value.Identity.RosterGeneration - 1 }, null))).IsSuccess);
    }

    private static async ValueTask WardrobePublicMicrosoftSkinDownloadsAndUploadsWithoutPublicCredentials()
    {
        var accounts = WardrobeAccounts(); accounts.AddProfile(WardrobeMicrosoft());
        using var handler = new ParityWardrobeHttp(); using var http = new HttpClient(handler);
        using var skins = new AccountSkinService(accounts, http);
        var service = new AccountWardrobeService(accounts, http, skins);
        var stamp = (await service.ReadAsync(new())).Value.Identity;
        var preview = await service.ReadTextureAsync(new(stamp, SiteId: "littleskin", TextureId: 42));
        AssertTrue(preview.IsSuccess); AssertTrue(preview.Value.IsSlim); AssertTrue(preview.Value.Skin is not null);
        AssertTrue((await service.ApplyPublicAsync(new(stamp, "littleskin", 42, AccountWardrobeTextureKind.Skin))).IsSuccess);
        await skins.WhenIdle;
        var upload = handler.Requests.Single(request => request.Method == "POST" && request.Uri.AbsolutePath == "/minecraft/profile/skins");
        AssertTrue(upload.Body.AsSpan().IndexOf(SkinFixture()) >= 0);
        AssertTrue(Encoding.UTF8.GetString(upload.Body).Contains("slim", StringComparison.Ordinal));
        AssertEqual("PRIVATE-Alice", upload.Token!);
        AssertEqual("https://textures.minecraft.net/updated", accounts.GetProfile(0).Value.SkinAddress!);
        AssertTrue(handler.Requests.Where(request => request.Uri.Host == "littleskin.cn").All(request => request.Token is null));
    }

    private static async ValueTask WardrobeLittleSkinPublicEnsuresClosetAndVerifiesPlayerReadback()
    {
        foreach (bool cape in new[] { false, true })
        {
            var accounts = WardrobeAccounts(); accounts.AddProfile(ParityLittleSkin());
            var history = new WardrobeHistoryStore();
            using var handler = new ParityWardrobeHttp { PublicCape = cape };
            using var http = new HttpClient(handler); using var skins = new AccountSkinService(accounts, http);
            var service = new AccountWardrobeService(accounts, http, skins, history, littleSkin: new LittleSkinOAuthService(http));
            var stamp = (await service.ReadAsync(new())).Value.Identity;
            AssertTrue((await service.ApplyPublicAsync(new(stamp, "littleskin", 42,
                cape ? AccountWardrobeTextureKind.Cape : AccountWardrobeTextureKind.Skin))).IsSuccess);
            await skins.WhenIdle;
            var requests = handler.Requests;
            int ensure = Array.FindIndex(requests, request => request.Method == "POST" && request.Uri.AbsolutePath == "/api/closet");
            int apply = Array.FindIndex(requests, request => request.Method == "PUT" && request.Uri.AbsolutePath == "/api/players/7/textures");
            int readback = Array.FindIndex(requests, apply + 1, request => request.Uri.AbsolutePath == "/api/players");
            AssertTrue(ensure >= 0 && apply > ensure && readback > apply);
            AssertEqual("PROVIDER", requests[ensure].Token!); AssertEqual("PROVIDER", requests[apply].Token!);
            AssertEqual(cape ? "cape=42" : "skin=42", Encoding.UTF8.GetString(requests[apply].Body));
            AssertTrue(requests.Where(request => request.Uri.AbsolutePath == "/skinlib/info/42").All(request => request.Token is null));
            string address = "https://littleskin.cn/textures/" + CatalogHash;
            if (!cape) AssertEqual(address, accounts.GetProfile(0).Value.SkinAddress!);
            AssertTrue((await history.LoadAsync()).Any(entry => entry.Address == address
                && entry.Kind == (cape ? AccountWardrobeTextureKind.Cape : AccountWardrobeTextureKind.Skin)));
        }
        var failedAccounts = WardrobeAccounts(); failedAccounts.AddProfile(ParityLittleSkin());
        using var failedHandler = new ParityWardrobeHttp { IgnoreApply = true };
        using var failedHttp = new HttpClient(failedHandler); using var failedSkins = new AccountSkinService(failedAccounts, failedHttp);
        var failedService = new AccountWardrobeService(failedAccounts, failedHttp, failedSkins, littleSkin: new LittleSkinOAuthService(failedHttp));
        var failedStamp = (await failedService.ReadAsync(new())).Value.Identity;
        AssertFalse((await failedService.ApplyPublicAsync(new(failedStamp, "littleskin", 42, AccountWardrobeTextureKind.Skin))).IsSuccess);
        AssertEqual("https://textures.minecraft.net/texture/original", failedAccounts.GetProfile(0).Value.SkinAddress!);
    }

    private static async ValueTask WardrobeCloudKeepsUploadsAndSiteReferencesDistinctAndAdmitted()
    {
        var accounts = WardrobeAccounts(); accounts.AddProfile(WardrobeMicrosoft() with { Kind = LaunchProfileKind.NCloud });
        using var handler = new ParityWardrobeHttp(); using var http = new HttpClient(handler);
        using var skins = new AccountSkinService(accounts, http);
        var cloud = new ParityCloudWardrobe();
        var service = new AccountWardrobeService(accounts, http, skins, new WardrobeHistoryStore(), cloud);
        var read = await service.ReadAsync(new()); AssertTrue(read.IsSuccess); AssertTrue(read.Value.CanUploadSkin);
        AssertFalse(read.Value.CanChooseCape);
        AssertTrue((await service.UploadSkinAsync(new(read.Value.Identity, SkinFixture(), "local.png", true))).IsSuccess);
        await skins.WhenIdle; AssertEqual(1, cloud.Uploads); AssertEqual(0, cloud.References);
        AssertTrue(cloud.LastBytes!.AsSpan().SequenceEqual(SkinFixture())); AssertTrue(cloud.LastSlim);
        read = await service.ReadAsync(new()); AssertTrue(read.IsSuccess);
        int beforeImages = handler.Requests.Count(request => request.Uri.AbsolutePath.StartsWith("/textures/", StringComparison.Ordinal));
        AssertTrue((await service.ApplyPublicAsync(new(read.Value.Identity, "littleskin", 42, AccountWardrobeTextureKind.Skin))).IsSuccess);
        await skins.WhenIdle; AssertEqual(1, cloud.Uploads); AssertEqual(1, cloud.References);
        AssertEqual("littleskin", cloud.LastSite!); AssertEqual(42L, cloud.LastTextureId); AssertTrue(cloud.LastSlim);
        AssertEqual(beforeImages, handler.Requests.Count(request => request.Uri.AbsolutePath.StartsWith("/textures/", StringComparison.Ordinal)));
        AssertEqual("https://cloud.example.test/site", accounts.GetProfile(0).Value.SkinAddress!);
        AssertFalse(cloud.LastProfile.ToString().Contains("PRIVATE", StringComparison.Ordinal));
        read = await service.ReadAsync(new());
        cloud.Pause = true;
        var pending = service.UploadSkinAsync(new(read.Value.Identity, SkinFixture(), "late.png", false)).AsTask();
        await cloud.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AssertTrue(accounts.ReplaceProfile(0, accounts.GetProfile(0).Value with { AccessToken = "NEW" }).IsSuccess);
        cloud.Release.SetResult(); AssertFalse((await pending).IsSuccess);
        AssertEqual("https://cloud.example.test/site", accounts.GetProfile(0).Value.SkinAddress!);
    }

    private static async ValueTask WardrobeMissingPortsAndThirdPartyHandoffsStayTruthful()
    {
        foreach (var kind in new[] { LaunchProfileKind.LittleSkin, LaunchProfileKind.NCloud, LaunchProfileKind.Offline, LaunchProfileKind.ThirdParty })
        {
            var accounts = WardrobeAccounts(); accounts.AddProfile(WardrobeMicrosoft() with { Kind = kind });
            using var handler = new ParityWardrobeHttp(); using var http = new HttpClient(handler);
            using var skins = new AccountSkinService(accounts, http);
            var service = new AccountWardrobeService(accounts, http, skins);
            var read = await service.ReadAsync(new()); AssertTrue(read.IsSuccess);
            AssertFalse(read.Value.CanUploadSkin); AssertFalse(read.Value.CanChooseCape);
            AssertEqual(AccountWardrobeCapeState.Unsupported, read.Value.CapeState);
            AssertTrue(read.Value.UnavailableReason is not null);
            int before = handler.Requests.Length;
            AssertFalse((await service.UploadSkinAsync(new(read.Value.Identity, SkinFixture(), "skin.png", false))).IsSuccess);
            AssertFalse((await service.ApplyPublicAsync(new(read.Value.Identity, "littleskin", 42, AccountWardrobeTextureKind.Skin))).IsSuccess);
            AssertEqual(before, handler.Requests.Length);
        }
        foreach ((string address, string? expected) in new[]
        {
            ("https://skin.example.test/blessing/api/yggdrasil/authserver/", "https://skin.example.test/blessing/user/profile"),
            ("https://skin.example.test/blessing/API/YGGDRASIL", "https://skin.example.test/blessing/user/profile"),
            ("https://skin.example.test/auth", "https://skin.example.test/auth"),
            ("https://localhost/api/yggdrasil", (string?)null),
            ("https://skin.example.test/api/yggdrasil?secret=token", (string?)null)
        })
        {
            var accounts = WardrobeAccounts(); accounts.AddProfile(WardrobeMicrosoft() with { Kind = LaunchProfileKind.ThirdParty, AuthServer = address });
            using var http = new HttpClient(new ParityWardrobeHttp()); using var skins = new AccountSkinService(accounts, http);
            var read = await new AccountWardrobeService(accounts, http, skins).ReadAsync(new());
            AssertTrue(read.IsSuccess); AssertEqual(expected, read.Value.ManageUri);
        }
    }

    private static async ValueTask WardrobeCardAndPublicRoutesRejectUnprojectedOrMismatchedSources()
    {
        var accounts = WardrobeAccounts(); accounts.AddProfile(WardrobeMicrosoft());
        var history = new WardrobeHistoryStore();
        await history.RecordAsync([new("other", "Other", AccountWardrobeTextureKind.Skin,
            "https://textures.minecraft.net/history", false, DateTimeOffset.UtcNow)]);
        using var handler = new ParityWardrobeHttp(); using var http = new HttpClient(handler);
        using var skins = new AccountSkinService(accounts, http);
        var service = new AccountWardrobeService(accounts, http, skins, history);
        var read = await service.ReadAsync(new()); AssertTrue(read.IsSuccess);
        var card = read.Value.Skins.Single(); var stamp = read.Value.Identity;
        int before = handler.Requests.Length;
        AssertFalse((await service.ApplyCardAsync(new(stamp, "https://untrusted.example.test/skin"))).IsSuccess);
        AssertFalse((await service.ReadTextureAsync(new(stamp, card.Id, "littleskin", 42))).IsSuccess);
        AssertFalse((await service.ReadTextureAsync(new(stamp, card.Id, Kind: AccountWardrobeTextureKind.Cape))).IsSuccess);
        AssertFalse((await service.ApplyPublicAsync(new(stamp, "https://evil.example.test", 42, AccountWardrobeTextureKind.Skin))).IsSuccess);
        AssertFalse((await service.ApplyPublicAsync(new(stamp, "littleskin", 42, AccountWardrobeTextureKind.Cape))).IsSuccess);
        AssertFalse((await service.ApplyPublicAsync(new(stamp, "littleskin", 42, (AccountWardrobeTextureKind)9))).IsSuccess);
        AssertEqual(before, handler.Requests.Length);
        handler.PublicCape = true;
        AssertFalse((await service.ApplyPublicAsync(new(stamp, "littleskin", 42, AccountWardrobeTextureKind.Skin))).IsSuccess);
        AssertEqual(0, handler.Requests.Count(request => request.Method == "POST"));
        handler.PublicCape = false;
        AssertTrue((await service.ReadTextureAsync(new(stamp, card.Id))).IsSuccess);
        AssertTrue((await service.ApplyCardAsync(new(stamp, card.Id))).IsSuccess);
        await skins.WhenIdle;
        AssertFalse((await service.ApplyCardAsync(new(stamp, card.Id))).IsSuccess);
        var refreshed = await service.ReadAsync(new()); AssertTrue(refreshed.IsSuccess);
        AssertFalse((await service.ApplyCardAsync(new(refreshed.Value.Identity, refreshed.Value.CapeCards.Single().Id))).IsSuccess);
    }

    private static async ValueTask WardrobeLatePublicTexturesAndMutationsRetireAcrossAccountChanges()
    {
        foreach (string route in new[] { "public_preview", "card_preview", "public_apply" })
            foreach (bool cancel in new[] { false, true })
            {
                var accounts = WardrobeAccounts(); accounts.AddProfile(WardrobeMicrosoft()); accounts.AddProfile(WardrobeMicrosoft("Bob"));
                var history = new WardrobeHistoryStore();
                await history.RecordAsync([new("other", "Historical", AccountWardrobeTextureKind.Skin,
                "https://littleskin.cn/textures/" + CatalogHash, false, DateTimeOffset.UtcNow)]);
                using var handler = new ParityWardrobeHttp(); using var http = new HttpClient(handler);
                using var skins = new AccountSkinService(accounts, http);
                var service = new AccountWardrobeService(accounts, http, skins, history);
                var read = (await service.ReadAsync(new())).Value;
                var stamp = read.Identity;
                handler.PausePublicImage = true;
                using var stop = new CancellationTokenSource();
                var image = route == "public_apply" ? null : service.ReadTextureAsync(route == "card_preview"
                    ? new(stamp, read.Skins.Single(card => card.Title == "Historical").Id)
                    : new(stamp, SiteId: "littleskin", TextureId: 42), stop.Token).AsTask();
                var mutation = route == "public_apply" ? service.ApplyPublicAsync(new(stamp, "littleskin", 42, AccountWardrobeTextureKind.Skin), stop.Token).AsTask() : null;
                await handler.ImageEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (cancel) stop.Cancel();
                else { AssertTrue(accounts.SelectProfile(1) is null); AssertTrue(accounts.SelectProfile(0) is null); }
                handler.ImageRelease.SetResult();
                if (image is not null) AssertFalse((await image).IsSuccess);
                if (mutation is not null) AssertFalse((await mutation).IsSuccess);
                AssertEqual(0, handler.Requests.Count(request => request.Method == "POST"));
                AssertEqual("https://textures.minecraft.net/texture/original", accounts.GetProfile(0).Value.SkinAddress!);
            }
    }

    private static async ValueTask WardrobeHistoryFailureDoesNotRejectSuccessfulProviderMutation()
    {
        string root = CreateTempDirectory();
        try
        {
            string blocked = Path.Combine(root, "blocked");
            await File.WriteAllTextAsync(blocked, "A file cannot contain an Appearance directory.");
            var history = new WardrobeHistoryStore(blocked);
            var accounts = WardrobeAccounts(); accounts.AddProfile(WardrobeMicrosoft());
            using var handler = new ParityWardrobeHttp(); using var http = new HttpClient(handler);
            using var skins = new AccountSkinService(accounts, http);
            var service = new AccountWardrobeService(accounts, http, skins, history);
            var read = await service.ReadAsync(new()); AssertTrue(read.IsSuccess);
            AssertTrue((await service.UploadSkinAsync(new(read.Value.Identity, SkinFixture(), "local.png", false))).IsSuccess);
            await skins.WhenIdle;
            AssertEqual("https://textures.minecraft.net/updated", accounts.GetProfile(0).Value.SkinAddress!);
            var retained = await history.LoadAsync();
            AssertTrue(retained.Any(entry => entry.Address == "https://textures.minecraft.net/texture/original"));
            AssertTrue(retained.Any(entry => entry.Address == "https://textures.minecraft.net/updated"));
            AssertEqual(1, handler.Requests.Count(request => request.Method == "POST"));
        }
        finally { Directory.Delete(root, true); }
    }

    private static LittleSkinOAuthConfiguration ParityLittleSkinConfiguration() => new("client", "", new("https://example.test/callback"));
    private static async ValueTask WardrobeLittleSkinRetriesUnauthorizedSessionAndInventoryOnce()
    {
        foreach (string path in new[] { "/api/yggdrasil/authserver/oauth", "/api/players", "/api/closet" })
            foreach (bool forbidden in new[] { false, true })
            {
                var accounts = WardrobeAccounts(); accounts.AddProfile(ParityLittleSkin() with { RefreshToken = "REFRESH" });
                using var handler = new ParityWardrobeHttp { FaultPath = path, Forbidden = forbidden };
                using var http = new HttpClient(handler); using var skins = new AccountSkinService(accounts, http);
                var service = new AccountWardrobeService(accounts, http, skins, littleSkin: new LittleSkinOAuthService(http),
                    littleSkinConfiguration: ParityLittleSkinConfiguration());
                var read = await service.ReadAsync(new()); AssertTrue(read.IsSuccess);
                AssertEqual(AccountWardrobeCapeState.Loaded, read.Value.CapeState);
                AssertEqual(1, handler.Requests.Count(request => request.Uri.AbsolutePath == "/oauth/token"));
                AssertEqual("ROTATED-PROVIDER", accounts.GetProfile(0).Value.ProviderAccessToken);
                AssertEqual("ROTATED-REFRESH", accounts.GetProfile(0).Value.RefreshToken);
                var attempts = handler.Requests.Where(request => request.Uri.AbsolutePath == path).ToArray();
                AssertTrue(attempts.Length >= 2); AssertEqual("PROVIDER", attempts[0].Token!);
                AssertTrue(attempts.Skip(1).All(request => request.Token == "ROTATED-PROVIDER"));
                AssertFalse(read.Value.ToString().Contains("ROTATED-PROVIDER", StringComparison.Ordinal));
            }
    }

    private static async ValueTask WardrobeLittleSkinRetriesMutationReadbackAndGameUploadWithCapturedTokens()
    {
        foreach (string stage in new[] { "apply", "readback", "upload", "clear" })
        {
            var accounts = WardrobeAccounts(); accounts.AddProfile(ParityLittleSkin() with { RefreshToken = "REFRESH" });
            using var handler = new ParityWardrobeHttp(); using var http = new HttpClient(handler);
            using var skins = new AccountSkinService(accounts, http);
            var service = new AccountWardrobeService(accounts, http, skins, littleSkin: new LittleSkinOAuthService(http),
                littleSkinConfiguration: ParityLittleSkinConfiguration());
            var stamp = (await service.ReadAsync(new())).Value.Identity;
            handler.FaultPath = stage switch
            {
                "upload" => "/api/yggdrasil/api/user/profile/0123456789abcdef0123456789abcdef/skin",
                "readback" => "/api/players",
                _ => "/api/players/7/textures"
            };
            handler.FaultOnlyReadback = stage == "readback";
            var result = stage switch
            {
                "upload" => await service.UploadSkinAsync(new(stamp, SkinFixture(), "local.png", true)),
                "clear" => await service.SetCapeAsync(new(stamp, null)),
                _ => await service.ApplyPublicAsync(new(stamp, "littleskin", 42, AccountWardrobeTextureKind.Skin))
            };
            AssertTrue(result.IsSuccess); await skins.WhenIdle;
            AssertEqual(1, handler.Requests.Count(request => request.Uri.AbsolutePath == "/oauth/token"));
            AssertEqual("ROTATED-PROVIDER", accounts.GetProfile(0).Value.ProviderAccessToken);
            var mutations = handler.Requests.Where(request => request.Method == "PUT" && request.Uri.AbsolutePath == handler.FaultPath).ToArray();
            if (stage == "upload")
            {
                AssertEqual(2, mutations.Length); AssertEqual("GAME", mutations[0].Token!); AssertEqual("GAME-ROTATED", mutations[1].Token!);
                AssertTrue(mutations.All(request => request.Body.AsSpan().IndexOf(SkinFixture()) >= 0));
            }
            else if (stage != "readback")
            {
                AssertEqual(2, mutations.Length); AssertEqual("PROVIDER", mutations[0].Token!); AssertEqual("ROTATED-PROVIDER", mutations[1].Token!);
            }
            if (stage is "apply" or "readback")
                AssertEqual("https://littleskin.cn/textures/" + CatalogHash, accounts.GetProfile(0).Value.SkinAddress!);
        }
    }

    private static async ValueTask WardrobeLittleSkinRepeatedUnauthorizedPreservesRotatedCredentialsAndStops()
    {
        foreach (bool session in new[] { false, true })
        {
            var accounts = WardrobeAccounts(); accounts.AddProfile(ParityLittleSkin() with { RefreshToken = "REFRESH" });
            using var handler = new ParityWardrobeHttp { FaultPath = session ? "/api/yggdrasil/authserver/oauth" : "/api/players", RepeatedUnauthorized = true };
            using var http = new HttpClient(handler); using var skins = new AccountSkinService(accounts, http);
            var service = new AccountWardrobeService(accounts, http, skins, littleSkin: new LittleSkinOAuthService(http),
                littleSkinConfiguration: ParityLittleSkinConfiguration());
            var read = await service.ReadAsync(new()); AssertTrue(read.IsSuccess);
            AssertEqual(AccountWardrobeCapeState.LoadFailed, read.Value.CapeState);
            AssertEqual(1, handler.Requests.Count(request => request.Uri.AbsolutePath == "/oauth/token"));
            AssertEqual(2, handler.Requests.Count(request => request.Uri.AbsolutePath == handler.FaultPath));
            AssertEqual("ROTATED-PROVIDER", accounts.GetProfile(0).Value.ProviderAccessToken);
            AssertEqual("ROTATED-REFRESH", accounts.GetProfile(0).Value.RefreshToken);
            AssertEqual("https://textures.minecraft.net/texture/original", accounts.GetProfile(0).Value.SkinAddress!);
            AssertFalse(read.Value.ToString().Contains("REMOTE-PRIVATE", StringComparison.Ordinal));
        }
        var stageAccounts = WardrobeAccounts(); stageAccounts.AddProfile(ParityLittleSkin() with { RefreshToken = "REFRESH" });
        using var stageHandler = new ParityWardrobeHttp { FaultPath = "/api/yggdrasil/authserver/oauth", FailInventoryAfterRefresh = true };
        using var stageHttp = new HttpClient(stageHandler); using var stageSkins = new AccountSkinService(stageAccounts, stageHttp);
        var stageService = new AccountWardrobeService(stageAccounts, stageHttp, stageSkins, littleSkin: new LittleSkinOAuthService(stageHttp),
            littleSkinConfiguration: ParityLittleSkinConfiguration());
        var stages = await stageService.ReadAsync(new()); AssertTrue(stages.IsSuccess);
        AssertEqual(AccountWardrobeCapeState.LoadFailed, stages.Value.CapeState);
        AssertEqual(1, stageHandler.Requests.Count(request => request.Uri.AbsolutePath == "/oauth/token"));
    }

    private static async ValueTask WardrobeLittleSkinLateRefreshCannotCommitAcrossSelectionOrCancellation()
    {
        foreach (bool cancel in new[] { false, true })
            foreach (bool mutation in new[] { false, true })
            {
                var accounts = WardrobeAccounts(); accounts.AddProfile(ParityLittleSkin() with { RefreshToken = "REFRESH" });
                accounts.AddProfile(WardrobeMicrosoft("Bob"));
                using var handler = new ParityWardrobeHttp(); using var http = new HttpClient(handler);
                using var skins = new AccountSkinService(accounts, http);
                var service = new AccountWardrobeService(accounts, http, skins, littleSkin: new LittleSkinOAuthService(http),
                    littleSkinConfiguration: ParityLittleSkinConfiguration());
                var stamp = (await service.ReadAsync(new())).Value.Identity;
                handler.FaultPath = mutation ? "/api/players/7/textures" : "/api/players";
                handler.PauseProviderRefresh = true;
                using var stop = new CancellationTokenSource();
                var reading = mutation ? null : service.ReadAsync(new(), stop.Token).AsTask();
                var writing = mutation ? service.ApplyPublicAsync(new(stamp, "littleskin", 42, AccountWardrobeTextureKind.Skin), stop.Token).AsTask() : null;
                await handler.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var before = accounts.GetProfile(0).Value;
                if (cancel) stop.Cancel();
                else { AssertTrue(accounts.SelectProfile(1) is null); AssertTrue(accounts.SelectProfile(0) is null); }
                handler.RefreshRelease.SetResult();
                if (reading is not null) AssertFalse((await reading).IsSuccess);
                if (writing is not null) AssertFalse((await writing).IsSuccess);
                AssertEqual(before, accounts.GetProfile(0).Value);
                AssertEqual(1, handler.Requests.Count(request => request.Uri.AbsolutePath == "/oauth/token"));
                AssertFalse(handler.Requests.Any(request => request.Token == "ROTATED-PROVIDER"));
            }
    }

    private static async ValueTask WardrobeLittleSkinPublicDenialsNeverRefreshProviderCredentials()
    {
        foreach (string path in new[] { "/skinlib/info/42", "/textures/" + CatalogHash })
        {
            var accounts = WardrobeAccounts(); accounts.AddProfile(ParityLittleSkin() with { RefreshToken = "REFRESH" });
            using var handler = new ParityWardrobeHttp(); using var http = new HttpClient(handler);
            using var skins = new AccountSkinService(accounts, http);
            var service = new AccountWardrobeService(accounts, http, skins, littleSkin: new LittleSkinOAuthService(http),
                littleSkinConfiguration: ParityLittleSkinConfiguration());
            var stamp = (await service.ReadAsync(new())).Value.Identity;
            var before = accounts.GetProfile(0).Value;
            handler.FailPublicPath = path;
            var preview = await service.ReadTextureAsync(new(stamp, SiteId: "littleskin", TextureId: 42));
            if (path.StartsWith("/textures/", StringComparison.Ordinal))
            { AssertTrue(preview.IsSuccess); AssertTrue(preview.Value.Skin is null); }
            else
            {
                AssertFalse(preview.IsSuccess);
                AssertFalse((await service.ApplyPublicAsync(new(stamp, "littleskin", 42, AccountWardrobeTextureKind.Skin))).IsSuccess);
            }
            AssertEqual(0, handler.Requests.Count(request => request.Uri.AbsolutePath == "/oauth/token"));
            AssertEqual(before, accounts.GetProfile(0).Value);
        }
    }

    private sealed record ParityWardrobeRequest(string Method, Uri Uri, string? Token, byte[] Body);
    private sealed class ParityWardrobeHttp : HttpMessageHandler
    {
        private readonly List<ParityWardrobeRequest> _requests = [];
        private int _capeReads;
        internal bool SessionTextures, EmptyCapes, FailCapes, RejectFirstCapeRead, PublicCape, IgnoreApply, PausePublicImage;
        internal string? FaultPath, FailPublicPath;
        internal bool Forbidden, RepeatedUnauthorized, FaultOnlyReadback, PauseProviderRefresh, FailInventoryAfterRefresh;
        private bool _applied;
        private long _skinId = 11, _capeId = 22;
        internal TaskCompletionSource ImageEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ImageRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RefreshEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RefreshRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ParityWardrobeRequest[] Requests { get { lock (_requests) return _requests.ToArray(); } }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var uri = request.RequestUri!; string path = uri.AbsolutePath;
            lock (_requests) _requests.Add(new(request.Method.Method, uri, request.Headers.Authorization?.Parameter, body));
            string? bearer = request.Headers.Authorization?.Parameter;
            if (path == FailPublicPath)
                return new(HttpStatusCode.Unauthorized) { Content = new StringContent("REMOTE-PRIVATE") };
            if (path == "/oauth/token")
            {
                if (PauseProviderRefresh) { RefreshEntered.TrySetResult(); await RefreshRelease.Task; }
                return CatalogJson("""{"access_token":"ROTATED-PROVIDER","refresh_token":"ROTATED-REFRESH","expires_in":3600}""");
            }
            if (path == FaultPath && (!FaultOnlyReadback || _applied)
                && (RepeatedUnauthorized || bearer is "PROVIDER" or "GAME")
                || FailInventoryAfterRefresh && path == "/api/players" && bearer == "ROTATED-PROVIDER")
                return new(Forbidden ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized) { Content = new StringContent("REMOTE-PRIVATE") };
            if (path == "/minecraft/profile")
            {
                if (FailCapes || RejectFirstCapeRead && Interlocked.Increment(ref _capeReads) == 1)
                    return new(HttpStatusCode.Unauthorized) { Content = new StringContent("REMOTE-PRIVATE") };
                return CatalogJson(EmptyCapes ? """{"capes":[]}""" : """{"capes":[{"id":"owned","alias":"Migrator","url":"https://textures.minecraft.net/owned-cape","state":"ACTIVE"}]}""");
            }
            if (path == "/minecraft/profile/skins")
                return CatalogJson("""{"skins":[{"url":"https://textures.minecraft.net/updated","state":"ACTIVE"}]}""");
            if (path.EndsWith("/session/minecraft/profile/0123456789abcdef0123456789abcdef", StringComparison.Ordinal) && SessionTextures)
                return CatalogJson("""{"textures":{"SKIN":{"url":"https://textures.minecraft.net/alice-skin","metadata":{"model":"slim"}},"CAPE":{"url":"https://textures.minecraft.net/session-cape"}}}""");
            if (path.EndsWith("/session/minecraft/profile/1123456789abcdef0123456789abcdef", StringComparison.Ordinal) && SessionTextures)
                return CatalogJson("""{"textures":{"SKIN":{"url":"https://textures.minecraft.net/bob-skin"}}}""");
            if (path == "/api/yggdrasil/authserver/oauth")
                return CatalogJson($$$"""{"accessToken":"{{{(bearer == "ROTATED-PROVIDER" ? "GAME-ROTATED" : "GAME")}}}","clientToken":"CLIENT","selectedProfile":{"id":"0123456789abcdef0123456789abcdef","name":"Alice"}}""");
            if (path == "/api/players")
                return CatalogJson($$"""[{"pid":7,"name":"Alice","tid_skin":{{_skinId}},"tid_cape":{{_capeId}}}]""");
            if (path == "/api/players/7/textures")
            {
                _applied = true;
                if (!IgnoreApply)
                {
                    string text = Encoding.UTF8.GetString(body);
                    if (text.StartsWith("skin=", StringComparison.Ordinal)) _skinId = long.Parse(text[5..], System.Globalization.CultureInfo.InvariantCulture);
                    if (text.StartsWith("cape=", StringComparison.Ordinal)) _capeId = long.Parse(text[5..], System.Globalization.CultureInfo.InvariantCulture);
                }
                return CatalogJson("""{"code":0}""");
            }
            if (path == "/api/closet")
                return CatalogJson(request.Method == HttpMethod.Post ? """{"code":0}""" : """{"last_page":1,"data":[]}""");
            if (path == "/skinlib/info/42")
                return CatalogJson($$"""{"tid":42,"name":"Public texture","type":"{{(PublicCape ? "cape" : "alex")}}","hash":"{{CatalogHash}}"}""");
            if (uri.Host == "littleskin.cn" && path.StartsWith("/textures/", StringComparison.Ordinal) && PausePublicImage)
            { ImageEntered.TrySetResult(); await ImageRelease.Task; } // Ignores cancellation to test final admission.
            if (uri.Host is "textures.minecraft.net" or "cloud.example.test" || uri.Host == "littleskin.cn" && path.StartsWith("/textures/", StringComparison.Ordinal))
                return new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(path.Contains("cape", StringComparison.Ordinal)
                    || PublicCape && uri.Host == "littleskin.cn" ? WardrobeCreatePng(height: 32) : SkinFixture())
                };
            return CatalogJson("{}");
        }
    }
    private sealed class ParityMicrosoftRefresh : IMicrosoftMinecraftAuthService
    {
        internal int Calls;
        internal bool Fail, Timeout;
        public Task<MicrosoftMinecraftLoginResult> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fail) return Task.FromException<MicrosoftMinecraftLoginResult>(Timeout
                ? new TaskCanceledException("REMOTE-PRIVATE") : new HttpRequestException("REMOTE-PRIVATE"));
            return Task.FromResult(new MicrosoftMinecraftLoginResult("Alice", "0123456789abcdef0123456789abcdef",
                "ROTATED-" + Calls, "REFRESH", null, true));
        }
        public Task<MicrosoftDeviceCodeInfo> RequestDeviceCodeAsync(string clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MicrosoftMinecraftLoginResult> CompleteDeviceLoginAsync(string clientId, MicrosoftDeviceCodeInfo deviceCode,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class ParityCloudWardrobe : INCloudWardrobePort
    {
        internal int Uploads, References;
        internal byte[]? LastBytes;
        internal string? LastSite;
        internal long LastTextureId;
        internal bool LastSlim, Pause;
        internal LaunchProfileView LastProfile;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CanManage(LaunchProfileView profile) => profile.Kind == LaunchProfileKind.NCloud;
        public async Task<AccountWardrobeCloudSkinResult> UploadSkinAsync(LaunchProfileView profile, byte[] pngBytes,
            bool isSlim, CancellationToken cancellationToken)
        {
            Uploads++; LastProfile = profile; LastBytes = pngBytes.ToArray(); LastSlim = isSlim;
            if (Pause) { Entered.SetResult(); await Release.Task; }
            return new("https://cloud.example.test/upload", "upload", "sha1");
        }
        public Task<AccountWardrobeCloudSkinResult> UseSiteSkinAsync(LaunchProfileView profile, string siteId,
            long textureId, bool isSlim, CancellationToken cancellationToken)
        {
            References++; LastProfile = profile; LastSite = siteId; LastTextureId = textureId; LastSlim = isSlim;
            return Task.FromResult(new AccountWardrobeCloudSkinResult("https://cloud.example.test/site", "site"));
        }
    }
}
