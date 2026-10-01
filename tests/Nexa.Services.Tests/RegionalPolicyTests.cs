using Nexa.Services.Accounts;
using Nexa.Services.Minecraft.Downloads;
using Nexa.Services.Resources;
using System.Net;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InternationalResourceTransportUsesOnlyOfficial()
    {
        List<string> requests = [];
        using var http = new HttpClient(new ResourceHttp(request =>
        {
            requests.Add(request.RequestUri!.Host);
            return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
        }));
        var source = new ResourceProviderHttp(http, "") { CountryPolicy = new("US") };
        bool failed = false;
        try { using var response = await source.ReadAsync(ResourceProvider.Modrinth, "search", true, default); }
        catch (IOException) { failed = true; }
        AssertTrue(failed);
        AssertEqual("api.modrinth.com", requests.Single());
        requests.Clear();
        failed = false;
        try { using var response = await source.ReadAsync(ResourceProvider.CurseForge, "mods/search", true, default); }
        catch (IOException) { failed = true; }
        AssertTrue(failed);
        AssertEqual(0, requests.Count); // Missing key never routes around the official API through MCIM.
    }

    private static void RegionalPolicyRequiresVerifiedOwnershipAtEveryWrite()
    {
        var accounts = CreateAccountService(new ThrowingProfilePort());
        accounts.ConfigureRegionalPolicy(new("US"));
        var offline = SampleProfile("Offline");
        var owner = SampleProfile("Owner") with { Kind = LaunchProfileKind.Microsoft, Uuid = "owner-uuid" };
        AssertFalse(accounts.AddProfile(offline).IsSuccess);
        AssertFalse(accounts.ImportProfiles([owner, offline]).IsSuccess);
        AssertEqual(0, accounts.GetViews().Count);
        AssertTrue(accounts.ImportProfiles([owner]).IsSuccess);
        AssertFalse(accounts.HasVerifiedMinecraftOwnership); // Imported kind/token are not proof.
        AssertFalse(accounts.AddProfile(offline).IsSuccess);
        AssertFalse(accounts.ReplaceProfile(0, offline).IsSuccess);
        accounts.RecordVerifiedOwnership(owner.Uuid);
        AssertTrue(accounts.AddProfile(offline).IsSuccess);
        AssertTrue(accounts.RemoveProfile(0).IsSuccess);
        AssertFalse(accounts.AddProfile(SampleProfile("Another")).IsSuccess);
        AssertTrue(accounts.AddProfile(owner).IsSuccess);
        AssertFalse(accounts.HasVerifiedMinecraftOwnership); // Removed evidence cannot resurrect.
        accounts.ConfigureRegionalPolicy(new("CN"));
        AssertTrue(accounts.AddProfile(SampleProfile("Mainland")).IsSuccess);
        AssertTrue(accounts.ImportProfiles([SampleProfile("Imported")]).IsSuccess);
    }

    private static void RegionalPolicyDisablesMirrorsOutsideMainland()
    {
        string[] official = ["https://official.example/file"], mirror = ["https://mirror.example/file"];
        foreach (string country in new[] { "US", "TW", "HK", "MO", "GB", "unknown" })
        {
            var policy = RegionalPolicy.Resolve(country);
            AssertTrue(policy.RequireMinecraftOwnership);
            AssertEqual(official[0], MinecraftDownloadSourcePlanner.OrderSources(official, mirror, false, policy).Single());
        }
        AssertEqual(mirror[0], MinecraftDownloadSourcePlanner.OrderSources(official, mirror, false, new("CN"))[0]);
        AssertFalse(RegionalPolicy.Resolve("cn").RequireMinecraftOwnership);
    }
}
