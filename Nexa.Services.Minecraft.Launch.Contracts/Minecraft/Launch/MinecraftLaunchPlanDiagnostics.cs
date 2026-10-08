using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Launch;

public sealed record MinecraftLaunchPlanDiagnosticQuery(string InstanceDirectory);
public sealed record MinecraftLaunchPlanDiagnosticSnapshot(string InstanceDirectory, DateTimeOffset? CapturedAt,
    string JavaExecutablePath, string WorkingDirectory, string MainClass, string Loader, int HeapLimitMiB,
    bool SafeLaunch, IReadOnlyList<string> Arguments, IReadOnlyList<string> ClasspathEntries,
    IReadOnlyList<string> NativeArchives, IReadOnlyList<string> EnvironmentNames, bool Truncated)
{
    public string Stage { get; init; } = "executor-before-process-start";
}

public static class MinecraftLaunchPlanDiagnosticContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.launch.plan-diagnostics.query");
}
