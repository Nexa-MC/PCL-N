using System.Diagnostics;

namespace Nexa.Platform;

public enum PlatformProcessQuality { Default, Background, Efficiency }
public readonly record struct PlatformJvmSample(long? CommitBytes, long? GpuLocalBytes,
    long? GpuSharedBytes, long? TreeWorkingSetBytes, long? TreeCpuMilliseconds, int? TreeProcessCount,
    string Source);
public sealed record PlatformSystemEvent(DateTimeOffset? Timestamp, string Source, string Message);
public readonly record struct PlatformJvmMemoryOutput(string? HeapInfo, string? NativeInfo);

public interface IPlatformJvmRuntime
{
    bool CpuSetsSupported { get; }
    bool QualitySupported { get; }
    bool CommitSupported { get; }
    bool GpuSupported { get; }
    bool TreeSupported { get; }
    bool SystemEventsSupported { get; }
    PlatformProcessControlResult SetCpuSets(Process process, IReadOnlyList<int> logicalProcessors);
    PlatformProcessControlResult SetQuality(Process process, PlatformProcessQuality quality);
    PlatformJvmSample ReadSample(Process process);
    ValueTask<PlatformJvmMemoryOutput> ReadJvmMemoryAsync(string javaExecutable, int processId);
    ValueTask<IReadOnlyList<PlatformSystemEvent>?> ReadSystemEventsAsync(int processId,
        DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken = default);
}
