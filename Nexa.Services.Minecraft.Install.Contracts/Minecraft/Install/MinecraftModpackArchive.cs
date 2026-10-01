



using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Install;

public sealed record MinecraftModpackPreview(string Path, string Sha256, string Name, string Version,
    string Format, string Game, InstallLoader? Loader, string? Build, string InstanceId, int RequiredFiles, int OptionalFiles);
public sealed record MinecraftModpackCommand(MinecraftModpackPreview Pack, string RootDirectory, bool IncludeOptional = false);
public static class MinecraftModpackContract
{
    public static readonly XsrSemanticId Install = XsrSemanticId.Parse("minecraft.modpack.install");
}
