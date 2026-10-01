

using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Install;

public enum MinecraftFolderKind { Unsupported, GameRoot, Version, Jar, Modpack }
public sealed record MinecraftFolderInspectQuery(string Path);
public sealed record MinecraftFolderInspection(MinecraftFolderKind Kind, string Path, string Name)
{
    public LocalJarArtifact? Jar { get; init; }
    public MinecraftModpackPreview? Modpack { get; init; }
}
public sealed record MinecraftFolderImportCommand(string Source, string TargetRoot);
public static class MinecraftFolderImportContract
{
    public static readonly XsrSemanticId Inspect = XsrSemanticId.Parse("minecraft.folder.inspect");
    public static readonly XsrSemanticId Import = XsrSemanticId.Parse("minecraft.folder.import");
}
