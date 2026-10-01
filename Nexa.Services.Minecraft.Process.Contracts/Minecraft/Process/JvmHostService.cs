



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
