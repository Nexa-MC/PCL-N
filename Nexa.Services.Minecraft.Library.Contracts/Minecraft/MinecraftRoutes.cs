
using Nexa.Services.Minecraft.Launch;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft;

public sealed record MinecraftVersionsQuery(string MinecraftRootDirectory);
public sealed record MinecraftInstancesQuery(string MinecraftRootDirectory);
public sealed record MinecraftStartCommand(string InstanceId, int AccountIndex)
{
    public string? MinecraftRootDirectory { get; init; }
}
public sealed record MinecraftLaunchCommand(MinecraftLaunchRequest Request);
public sealed record MinecraftCancelProcessCommand(Guid SessionId);
public sealed record MinecraftCancelLaunchCommand;
public sealed record MinecraftDecideJavaAcquisitionCommand(bool Approve);
public sealed record MinecraftSelectJavaCommand(string Path);
public sealed record MinecraftSelectJavaVersionCommand(int Major);
public sealed record MinecraftCrashAnalyzeQuery(IReadOnlyList<string> Evidence, string? Stage = null, string? LastClassName = null);

public static class MinecraftRouteIds
{
    public static readonly XsrSemanticId VersionsRead = XsrSemanticId.Parse("minecraft.versions.read");
    public static readonly XsrSemanticId InstancesRead = XsrSemanticId.Parse("minecraft.instances.read");
    public static readonly XsrSemanticId Start = XsrSemanticId.Parse("minecraft.start");
    public static readonly XsrSemanticId Launch = XsrSemanticId.Parse("minecraft.launch");
    public static readonly XsrSemanticId LaunchCancel = XsrSemanticId.Parse("minecraft.launch.cancel");
    public static readonly XsrSemanticId AcquireDecide = XsrSemanticId.Parse("minecraft.java.acquire.decide");
    public static readonly XsrSemanticId JavaVersionSelect = XsrSemanticId.Parse("minecraft.java.version.select");
    public static readonly XsrSemanticId JavaSelect = XsrSemanticId.Parse("minecraft.java.select");
    public static readonly XsrSemanticId ProcessCancel = XsrSemanticId.Parse("minecraft.process.cancel");
    public static readonly XsrSemanticId CrashAnalyze = XsrSemanticId.Parse("minecraft.crash.analyze");
}
