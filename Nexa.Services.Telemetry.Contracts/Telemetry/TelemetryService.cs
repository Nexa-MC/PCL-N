




namespace Nexa.Services.Telemetry;

/// <summary>One classified event; production transport enforces a fixed field allowlist.</summary>
public sealed record TelemetryEvent(
    string Name,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string> Properties)
{
    public TelemetryLevel Level { get; init; } = TelemetryLevel.Diagnostic;
}

/// <summary>
/// Upload port for telemetry batches. Implementations return whether the batch was accepted;
/// a rejected batch stays buffered.
/// </summary>
public interface ITelemetryTransport
{
    int MaximumBatchSize => int.MaxValue;
    Task<bool> SendAsync(IReadOnlyList<TelemetryEvent> batch, CancellationToken cancellationToken = default);
}
