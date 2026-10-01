




using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Install;

public sealed record MinecraftInstallEditQuery(string RootDirectory, string InstanceId);
public sealed record MinecraftInstallEditSnapshot(string RootDirectory, string InstanceId, string GameVersion,
    IReadOnlyList<InstallBuildSelection> Selection, string Fingerprint)
{
    public IReadOnlyList<MinecraftInstallManagedFile> ManagedMods { get; init; } = [];
    public string ModsRelativeDirectory { get; init; } = "mods";
}
public sealed record MinecraftInstallManagedFile(string Path, string Sha256, InstallLoader? Loader = null);
public static class MinecraftInstallEditContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.install.edit");
}
