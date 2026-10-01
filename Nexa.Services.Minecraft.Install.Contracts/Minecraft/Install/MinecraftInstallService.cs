


using System.Text.Json.Nodes;








using Nexa.Xsr;


namespace Nexa.Services.Minecraft.Install;

public sealed record MinecraftInstallAddon(InstallLoader Kind, string Version, IReadOnlyList<InstallDownload>? Downloads = null);

public sealed record MinecraftInstallCommand(
    string RootDirectory,
    string GameVersion,
    InstallLoader? Loader = null,
    string? LoaderBuild = null,
    IReadOnlyList<MinecraftInstallAddon>? Addons = null,
    string? InstanceName = null,
    string? EditFingerprint = null)
{
    internal string? ReuseRoot { get; init; }
    public LocalJarArtifact? LocalInstaller { get; init; }
    internal bool PreparingEdit { get; init; }
    internal string? ModsRelativeDirectory { get; init; }
    public bool? InheritVanilla { get; init; }
    public string? NewInstanceName { get; init; }
    public bool ForceReinstall { get; init; }
}

public sealed record MinecraftInstallResult(string InstanceId, string InstanceDirectory);

/// <summary>Resolves the remote version documents an install needs; tests inject in-memory fakes.</summary>
public interface IMinecraftInstallMetadataSource
{
    Task<JsonObject> FetchVanillaVersionJsonAsync(string gameVersion, CancellationToken cancellationToken);

    Task<JsonObject> FetchLoaderProfileJsonAsync(InstallLoader loader, string gameVersion, string build, CancellationToken cancellationToken);

    Task<JsonObject> FetchAssetIndexJsonAsync(string indexUrl, CancellationToken cancellationToken);
}

public static class MinecraftInstallRoutes
{
    public static readonly XsrSemanticId Run = XsrSemanticId.Parse("minecraft.install.run");
    public static readonly XsrSemanticId Stop = XsrSemanticId.Parse("minecraft.install.stop");
    public static readonly XsrSemanticId Recover = XsrSemanticId.Parse("minecraft.install.recovery");
}
