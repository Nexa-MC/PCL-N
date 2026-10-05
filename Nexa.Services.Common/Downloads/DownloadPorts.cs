namespace Nexa.Services.Downloads;

/// <summary>
/// One download request: ordered failover sources, one destination, and the ports that
/// produce connections and writers. Sources are tried in order until one completes.
/// </summary>
public sealed record DownloadRequest
{
    internal DownloadBandwidthBudget? BandwidthBudget { get; init; }
    /// <summary>False restarts each source from zero when no reliable artifact identity exists.</summary>
    public bool AllowResume { get; init; } = true;
    public required IReadOnlyList<string> Sources { get; init; }

    public required string DestinationPath { get; init; }

    public required Func<string, IDownloadConnection?> ConnectionFactory { get; init; }

    public int MaxParallelSegments { get; init; } = 1;

    public Func<string, IDownloadWriter?> WriterFactory { get; init; } =
        static path => new FileDownloadWriter(path);
}
