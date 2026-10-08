



using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Process;

public sealed record JvmHostEnvironment(
    string JavaExecutable,
    string WorkingDirectory,
    IReadOnlyList<string> JvmArguments,
    IReadOnlyList<string> GameArguments,
    IReadOnlyList<string> Classpath,
    string NativePath,
    string? Wrapper)
{
    public string? MainClass { get; init; }
}

public sealed record JvmHostObservation(
    Guid SessionId,
    string InstanceId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long LaunchDurationMilliseconds,
    long PeakWorkingSetBytes,
    long PeakPrivateBytes,
    long PeakThreadCount,
    long TotalProcessorMilliseconds,
    long HeapPeakBytes,
    long NativePeakBytes,
    long CommitPeakBytes,
    long GpuLocalPeakBytes,
    long GpuSharedPeakBytes,
    long IoReadBytes,
    long IoWriteBytes,
    string? CrashReportPath,
    string? HsErrPath,
    int? ExitCode,
    IReadOnlyList<string> StdoutTail,
    IReadOnlyList<string> StderrTail)
{
    public long CpuPeakPercent { get; init; }
    public long RuntimePhysicalP95Bytes { get; init; }
    public long RuntimeCommitP95Bytes { get; init; }
    public long RuntimeCpuP95Percent { get; init; }
    public long? MeasuredHeapPeakBytes { get; init; }
    public long? MeasuredNativePeakBytes { get; init; }
    public long? MeasuredCommitPeakBytes { get; init; }
    public long? MeasuredGpuLocalPeakBytes { get; init; }
    public long? MeasuredGpuSharedPeakBytes { get; init; }
    public long? MeasuredRuntimeHeapP95Bytes { get; init; }
    public long? MeasuredRuntimeCommitP95Bytes { get; init; }
    public long? MeasuredRuntimeGpuP95Bytes { get; init; }
    public long? PeakTreeWorkingSetBytes { get; init; }
    public int? PeakTreeProcessCount { get; init; }
    public bool CoreMetricsObserved { get; init; }
    public bool CpuPercentObserved { get; init; }
    public bool IoObserved { get; init; }
    public bool SystemEventsObserved { get; init; }
    public long? MeasuredCombinedGpuPeakBytes { get; init; }
    public IReadOnlyList<string> SystemEvents { get; init; } = [];
    public long LaunchWindowMilliseconds { get; init; }
    public long? MeasuredLaunchHeapPeakBytes { get; init; }
    public long? MeasuredLaunchNativePeakBytes { get; init; }
    public long? MeasuredLaunchPhysicalPeakBytes { get; init; }
    public long? MeasuredLaunchCommitPeakBytes { get; init; }
    public long? MeasuredLaunchGpuLocalPeakBytes { get; init; }
    public long? MeasuredLaunchGpuSharedPeakBytes { get; init; }
    public long? MeasuredLaunchCpuPeakPercent { get; init; }
    public long? MeasuredLaunchIoReadBytes { get; init; }
}

public static class JvmHostStateContract
{
    public static readonly XsrSemanticId ObservationsKey = XsrSemanticId.Parse("observation.jvm.sessions");
    public static readonly XsrSemanticId SamplesKey = XsrSemanticId.Parse("observation.jvm.samples");
    public static readonly XsrSemanticId ContextsKey = XsrSemanticId.Parse("observation.jvm.contexts");
    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        builder.Collection<JvmHostObservation, Guid>(ObservationsKey, "Nexa.Services.Minecraft.Process.JvmHost",
            static observation => observation.SessionId);
        builder.Collection<JvmRunSample, string>(SamplesKey, "Nexa.Services.Minecraft.Process.JvmHost", static sample => sample.Key);
        builder.Collection<JvmRunContext, Guid>(ContextsKey, "Nexa.Services.Minecraft.Process.JvmHost", static context => context.SessionId);
    }
}

public sealed record JvmHostControlResult(bool Succeeded, string Code, string Message);
