using Nexa.Xsr;

namespace Nexa.Services.Network;

public sealed record NetworkTraceQuery;
public sealed record NetworkManualProbeQuery;
public sealed record NetworkEndpointObservation(string Host, DateTimeOffset ObservedAt, int? StatusCode,
    double ElapsedMilliseconds, string? ErrorKind);
public sealed record NetworkManualProbeSnapshot(DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    IReadOnlyList<NetworkEndpointObservation> Endpoints);

public static class NetworkDiagnosticsContract
{
    public static readonly XsrSemanticId Trace = XsrSemanticId.Parse("network.diagnostics.trace");
    public static readonly XsrSemanticId Probe = XsrSemanticId.Parse("network.diagnostics.probe");
}
