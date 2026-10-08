using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Launch;

public sealed record MinecraftSafeLaunchSessionCommand(bool Required);
public sealed record MinecraftSafeLaunchSessionQuery;
public sealed record MinecraftSafeLaunchSessionSnapshot(bool Required);
public static class MinecraftSafeLaunchSessionContract
{
    public static readonly XsrSemanticId Set = XsrSemanticId.Parse("minecraft.launch.safe-mode.set");
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.launch.safe-mode.query");
}
