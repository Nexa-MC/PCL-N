using System.Collections.Concurrent;
using System.Net;
using Nexa.Services.Network;
using Nexa.Services.Resources;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ManualNetworkProbeUsesFixedAnonymousBoundedRequests()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentBag<string> requested = [];
        int active = 0, maximum = 0;
        var service = new NetworkManualProbeService(() => new HttpClient(new AsyncProbeHandler(async (request, token) =>
        {
            AssertEqual(HttpMethod.Head, request.Method);
            AssertEqual("https", request.RequestUri!.Scheme); AssertEqual("/", request.RequestUri.AbsolutePath);
            AssertEqual("", request.RequestUri.Query); AssertEqual("", request.RequestUri.UserInfo);
            AssertTrue(request.Content is null); AssertFalse(request.Headers.Any());
            AssertTrue(request.Options.TryGetValue(new HttpRequestOptionsKey<bool>("Nexa.Network.DiagnosticProbe"), out bool diagnostic) && diagnostic);
            requested.Add(request.RequestUri.Host);
            int current = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref maximum); } while (current > previous && Interlocked.CompareExchange(ref maximum, current, previous) != previous);
            entered.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                if (request.RequestUri.Host == "api.curseforge.com") throw new HttpRequestException("private-token=must-not-be-retained");
                return new(HttpStatusCode.NotFound) { Content = new ProbeUnreadBody() };
            }
            finally { Interlocked.Decrement(ref active); }
        })));
        Task<NetworkManualProbeSnapshot> running = service.ProbeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        bool secondRejected = false;
        try { await service.ProbeAsync(); } catch (InvalidOperationException) { secondRejected = true; }
        AssertTrue(secondRejected); release.SetResult();
        var result = await running;
        AssertEqual(5, result.Endpoints.Count); AssertEqual(5, requested.Distinct(StringComparer.Ordinal).Count());
        AssertTrue(maximum <= 2); AssertTrue(result.CompletedAt >= result.StartedAt);
        AssertTrue(result.Endpoints.Where(entry => entry.Host != "api.curseforge.com").All(entry => entry.StatusCode == 404));
        AssertEqual("HttpRequestException", result.Endpoints.Single(entry => entry.Host == "api.curseforge.com").ErrorKind);
        AssertFalse(result.ToString().Contains("private-token", StringComparison.Ordinal));
        await service.ProbeAsync(); // Completed observations do not hold the single-round gate.

        int canceledActive = 0;
        var cancellationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelService = new NetworkManualProbeService(() => new HttpClient(new AsyncProbeHandler(async (_, token) =>
        {
            Interlocked.Increment(ref canceledActive); cancellationStarted.TrySetResult();
            try { await blocked.Task.WaitAsync(token); return new(HttpStatusCode.OK); }
            finally { Interlocked.Decrement(ref canceledActive); }
        })));
        using var stop = new CancellationTokenSource();
        Task<NetworkManualProbeSnapshot> cancelRun = cancelService.ProbeAsync(stop.Token);
        await cancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel();
        bool canceled = false;
        try { await cancelRun; } catch (OperationCanceledException) { canceled = true; }
        AssertTrue(canceled); AssertEqual(0, canceledActive); // The round waits every admitted child before returning.
        AssertFalse(NetworkProviderAdmission.IsAllowed(new() { MirrorProviderEnabled = false }, new("https://mod.mcimirror.top/modrinth/v2/search")));
        AssertFalse(NetworkProviderAdmission.IsAllowed(new() { ModrinthProviderEnabled = false }, new("https://mod.mcimirror.top/modrinth/v2/search")));
        AssertFalse(NetworkProviderAdmission.IsAllowed(new() { CurseForgeProviderEnabled = false }, new("https://mod.mcimirror.top/curseforge/v1/mods")));
        AssertTrue(NetworkProviderAdmission.IsAllowed(new() { MirrorProviderEnabled = false }, new("https://api.minecraftservices.com/minecraft/profile")));
    }

    private static async ValueTask ResourceDependenciesRespectOptOutWithoutOmittingRequiredGraph()
    {
        string[] games = ["1.21.1"], loaders = ["fabric"];
        ResourceVersion Version(string id, params ResourceDependency[] dependencies) => new(id + "1", id, "1", "release",
            games, loaders, "2026-10-08", "https://modrinth.com/project/" + id)
        { ProjectId = id, Dependencies = dependencies, File = new(id + ".jar", "https://cdn.modrinth.com/data/a.jar", 3, null, new string('a', 128)) };
        var source = new DependencyCatalog([Version("A", new ResourceDependency("B", "B1", "required"), new ResourceDependency("D", "D1", "optional")),
            Version("B", new ResourceDependency("C", "C1", "required")), Version("C"), Version("D")]);
        var command = new ResourceModInstallCommand(new(ResourceProvider.Modrinth, "A"), "A1", new("root", "instance"));
        var context = new ResourceInstanceContext("1.21.1", "fabric", [], null);
        var planner = new ResourceDependencyPlanner(source) { AutoInstallDependencies = false };
        async Task Reject(ResourceInstanceContext current)
        {
            bool rejected = false;
            try { await planner.PreviewAsync(command, current, default); } catch (InvalidDataException) { rejected = true; }
            AssertTrue(rejected);
        }
        await Reject(context);
        var installedB = new ResourceInstalledFile(new(ResourceProvider.Modrinth, "B"), "B1", "B.jar", "", true);
        var installedC = new ResourceInstalledFile(new(ResourceProvider.Modrinth, "C"), "C1", "C.jar", "", true);
        await Reject(context with { Installed = [installedB] }); // An installed dependency still requires its complete required graph.
        var ready = context with { Installed = [installedB, installedC] };
        AssertEqual("A", string.Join(',', (await planner.PlanAsync(command, ready, default)).Select(item => item.ProjectId)));
        var optional = await planner.PlanAsync(command with { OptionalDependencies = [new(ResourceProvider.Modrinth, "D")] }, ready, default);
        AssertEqual("D,A", string.Join(',', optional.Select(item => item.ProjectId)));
        await Reject(ready with { Installed = [installedB with { Enabled = false }, installedC] });
        var automatic = await new ResourceDependencyPlanner(source).PlanAsync(command, context, default);
        AssertEqual("C,B,A", string.Join(',', automatic.Select(item => item.ProjectId)));
    }

    private static async ValueTask ResourceSourcePriorityAffectsRequestsAndObservesActualSelection()
    {
        string priority = "official-first";
        List<string> requests = [];
        using var http = new HttpClient(new ResourceHttp(request =>
        {
            requests.Add(request.RequestUri!.Host);
            AssertFalse(request.Headers.Contains("x-api-key"));
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        var provider = new ResourceProviderHttp(http) { CountryPolicy = new("CN"), SourcePriority = () => priority };
        using (await provider.ReadAsync(ResourceProvider.Modrinth, "search?query=private-query", true, default)) { }
        AssertEqual("api.modrinth.com", requests.Last());
        AssertTrue(provider.LastResolution is { RequestedMirrorFirst: true, EffectiveMirrorFirst: false, SelectedHost: "api.modrinth.com" });
        AssertFalse(provider.LastResolution!.ToString().Contains("private-query", StringComparison.Ordinal));
        priority = "mirrors-first";
        using (await provider.ReadAsync(ResourceProvider.Modrinth, "search", false, default)) { }
        AssertEqual("mod.mcimirror.top", requests.Last());
        AssertTrue(provider.LastResolution is { RequestedMirrorFirst: false, EffectiveMirrorFirst: true, SelectedHost: "mod.mcimirror.top" });
        priority = "follow-request";
        using (await provider.ReadAsync(ResourceProvider.Modrinth, "search", false, default)) { }
        AssertEqual("api.modrinth.com", requests.Last());
        var abroad = new ResourceProviderHttp(http) { CountryPolicy = new("US"), SourcePriority = () => "mirrors-first" };
        using (await abroad.ReadAsync(ResourceProvider.Modrinth, "search", true, default)) { }
        AssertEqual("api.modrinth.com", requests.Last());
        AssertEqual(1, abroad.LastResolution!.CandidateHosts.Count);
    }

    private sealed class AsyncProbeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class ProbeUnreadBody : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("The endpoint probe must not read response bodies.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
