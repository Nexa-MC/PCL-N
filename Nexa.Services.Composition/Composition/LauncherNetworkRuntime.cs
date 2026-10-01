using Nexa.Services.Foundation;
using Nexa.Services.Rollouts;
using Nexa.Services.Updates;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

/// <summary>Owns update policy and its HTTP pool for one host session.</summary>
public sealed class LauncherNetworkRuntime : IDisposable, IAsyncDisposable
{
    public HttpClient Http { get; } = Nexa.Services.Downloads.PooledHttpClient.Create(allowAutoRedirect: false);
    public string RuntimeId { get; }
    public RolloutService Rollouts { get; }
    public NexaUpdateService Updates { get; }
    public XsrQueryRouter Queries { get; }

    public LauncherNetworkRuntime(FoundationHost host, string settingsFolder, LauncherBuildIdentity build)
    {
        Http.Timeout = TimeSpan.FromSeconds(20);
        RuntimeId = (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-"
            + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        Rollouts = new(Http, host.StateStore, Path.Combine(settingsFolder, "rollout-seed"), build.Channel, RuntimeId);
        Updates = new(Http, Rollouts);
        Queries = NexaUpdateRuntimeComposer.Compose(Updates);
    }
    public void Dispose() { Rollouts.Dispose(); Http.Dispose(); }
    public async ValueTask DisposeAsync() { await Rollouts.DisposeAsync().ConfigureAwait(false); Http.Dispose(); }
}
