using System.Diagnostics;

namespace Nexa.Services.Network;

/// <summary>Fixed anonymous endpoint observations, without arbitrary URL input or response bodies.</summary>
public sealed class NetworkManualProbeService(Func<HttpClient> createClient)
{
    private static readonly string[] Hosts = ["piston-meta.mojang.com", "api.modrinth.com", "api.curseforge.com", "bmclapi2.bangbang93.com", "mod.mcimirror.top"];
    private static readonly HttpRequestOptionsKey<bool> DiagnosticProbe = new("Nexa.Network.DiagnosticProbe");
    private int _active;

    public async Task<NetworkManualProbeSnapshot> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) throw new InvalidOperationException("已有网络探测正在运行。");
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(10));
            using HttpClient client = createClient();
            using SemaphoreSlim parallel = new(2);
            async Task<NetworkEndpointObservation> ProbeHostAsync(string host)
            {
                await parallel.WaitAsync(budget.Token).ConfigureAwait(false);
                long started = Stopwatch.GetTimestamp();
                try
                {
                    using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                    requestBudget.CancelAfter(TimeSpan.FromSeconds(3));
                    using var request = new HttpRequestMessage(HttpMethod.Head, "https://" + host + "/");
                    request.Options.Set(DiagnosticProbe, true);
                    try
                    {
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestBudget.Token).ConfigureAwait(false);
                        return new(host, DateTimeOffset.UtcNow, (int)response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);
                    }
                    catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return new(host, DateTimeOffset.UtcNow, null, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                            error is OperationCanceledException ? "Timeout" : error.GetType().Name);
                    }
                }
                finally { parallel.Release(); }
            }
            NetworkEndpointObservation[] results = await Task.WhenAll(Hosts.Select(ProbeHostAsync)).ConfigureAwait(false);
            return new(startedAt, DateTimeOffset.UtcNow, Array.AsReadOnly(results));
        }
        finally { Volatile.Write(ref _active, 0); }
    }
}
