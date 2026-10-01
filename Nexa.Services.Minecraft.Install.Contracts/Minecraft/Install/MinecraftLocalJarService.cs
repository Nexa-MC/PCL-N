





using Nexa.Xsr;


namespace Nexa.Services.Minecraft.Install;

public enum LocalJarAction { Mod, CorePatch, Loader }
public sealed record LocalJarArtifact(string Path, string Sha256, string? Game = null, InstallLoader? Loader = null, string? Build = null);
public sealed record MinecraftLocalJarCommand(LocalJarArtifact Artifact, string Root, string InstanceId, LocalJarAction Action);
public static class MinecraftLocalJarContract
{
    public static readonly XsrSemanticId Import = XsrSemanticId.Parse("minecraft.jar.import");
}
