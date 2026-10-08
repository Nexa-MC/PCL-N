using System.Net;
using Nexa.Services.Downloads;
using Nexa.Services.Network;
using Nexa.Services.Scheduling;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask NetworkProvidersRejectDisabledRequestsAndTraceWithoutSecrets()
    {
        var policy = new NetworkPreferences
        {
            ModrinthProviderEnabled = false,
            OfficialProviderEnabled = false,
            CurseForgeProviderEnabled = false,
            MirrorProviderEnabled = false,
            TraceEnabled = true
        };
        int actualRequests = 0, factories = 0;
        using var pool = new NetworkHttpClientPool(() => policy, (_, _) =>
        {
            factories++;
            return new NetworkDnsTestHandler(_ => { actualRequests++; return new(HttpStatusCode.OK) { Content = new StringContent("ok") }; });
        });
        using var client = pool.CreateClient();
        foreach (string host in new[] { "api.modrinth.com", "api.curseforge.com", "piston-data.mojang.com", "bmclapi2.bangbang93.com" })
        {
            bool rejected = false;
            try { using var response = await client.GetAsync("https://" + host + "/private?token=secret"); }
            catch (HttpRequestException) { rejected = true; }
            AssertTrue(rejected);
        }
        AssertEqual(0, actualRequests);
        foreach (string host in new[] { "api.minecraftservices.com", "login.microsoftonline.com", "updates.nexacl.org", "api.modrinth.com.attacker.test" })
            using (var response = await client.GetAsync("https://" + host + "/private?token=secret")) AssertEqual(HttpStatusCode.OK, response.StatusCode);
        AssertEqual(4, actualRequests);
        AssertTrue(pool.TraceSnapshot().All(trace => !trace.ToString().Contains("secret", StringComparison.Ordinal)
            && !trace.ToString().Contains("/private", StringComparison.Ordinal)));
        policy = policy with { ModrinthProviderEnabled = true };
        for (int index = 0; index < 270; index++) using (var response = await client.GetAsync("https://api.modrinth.com/ok")) { }
        AssertEqual(256, pool.TraceSnapshot().Count);
        policy = policy with { TraceEnabled = false };
        AssertEqual(0, pool.TraceSnapshot().Count);
        using (var response = await client.GetAsync("https://api.modrinth.com/ok")) { }
        AssertEqual(0, pool.TraceSnapshot().Count);
        AssertEqual(1, factories);
    }

    private static async ValueTask NetworkAutomaticDiagnosticsUseBoundedAnonymousEndpointProbe()
    {
        int probes = 0;
        using var pool = new NetworkHttpClientPool(() => new() { AutoDiagnose = true }, (_, _) => new NetworkDnsTestHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
            {
                AssertEqual("https://example.test/", request.RequestUri!.AbsoluteUri);
                AssertTrue(request.Headers.Authorization is null);
                Interlocked.Increment(ref probes);
                return new(HttpStatusCode.OK) { Content = new StringContent("") };
            }
            return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unreachable") };
        }));
        using var client = pool.CreateClient();
        for (int index = 0; index < 12; index++)
            using (var response = await client.GetAsync("https://example.test/private?token=secret")) AssertEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertTrue(SpinWait.SpinUntil(() => pool.TraceSnapshot().Any(trace => trace.Kind == "Probe"), TimeSpan.FromSeconds(3)));
        AssertEqual(1, probes);
        AssertTrue(pool.TraceSnapshot().All(trace => !trace.ToString().Contains("secret", StringComparison.Ordinal)));
    }
    private static async ValueTask BackgroundDownloadPolicyKeepsInteractiveTransfersAvailable()
    {
        string directory = CreateTempDirectory();
        try
        {
            XsrStateStoreBuilder builder = new(); DownloadService.DeclareState(builder);
            using var scheduler = new WorkScheduler();
            bool enabled = false;
            var downloads = new DownloadService(builder.Build()) { WorkScheduler = scheduler, BackgroundDownloadsEnabled = () => enabled };
            DownloadRequest Request(string name) => new()
            { Sources = ["memory"], DestinationPath = Path.Combine(directory, name), ConnectionFactory = _ => new FakeConnection(1, [7]) };
            using (scheduler.UsePriority(WorkPriority.Background))
            {
                bool rejected = false;
                try { await downloads.DownloadAsync(Request("background")); } catch (InvalidOperationException) { rejected = true; }
                AssertTrue(rejected); AssertFalse(File.Exists(Path.Combine(directory, "background")));
            }
            using (scheduler.UsePriority(WorkPriority.Interactive)) AssertTrue((await downloads.DownloadAsync(Request("interactive"))).Success);
            enabled = true;
            using (scheduler.UsePriority(WorkPriority.Background)) AssertTrue((await downloads.DownloadAsync(Request("enabled"))).Success);
        }
        finally { Directory.Delete(directory, true); }
    }
}
