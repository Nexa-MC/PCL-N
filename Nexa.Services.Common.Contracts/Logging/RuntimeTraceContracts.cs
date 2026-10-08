namespace Nexa.Services.Logging;

public sealed record RuntimeTraceEntry(string Kind, string SemanticId, string CorrelationId,
    long MonotonicTimestamp, string Detail, bool IsSuccess);
public sealed record RuntimeTraceSnapshot(int Capacity, long Dropped, IReadOnlyList<RuntimeTraceEntry> Entries);
public sealed record RuntimeTraceQuery;
public static class RuntimeTraceContract
{
    public static readonly Nexa.Xsr.XsrSemanticId Read = Nexa.Xsr.XsrSemanticId.Parse("logging.runtime.trace.read");
}
