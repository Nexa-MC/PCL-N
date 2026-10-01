

namespace Nexa.Services.Network;

/// <summary>One named reachability probe target.</summary>
public sealed record NetworkEndpointProbe(string Name, string Url);

/// <summary>One probe outcome: reachability, HTTP status, and round-trip latency.</summary>
public sealed record NetworkProbeResult(
    string Name,
    string Url,
    bool Reachable,
    int? StatusCode,
    double LatencyMilliseconds,
    string? Error)
{
    public static NetworkProbeResult Unreachable(NetworkEndpointProbe endpoint, string error) =>
        new(endpoint.Name, endpoint.Url, false, null, 0, error);
}
