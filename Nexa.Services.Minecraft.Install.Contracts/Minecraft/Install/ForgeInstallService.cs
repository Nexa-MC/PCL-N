

using System.Text.Json.Nodes;






namespace Nexa.Services.Minecraft.Install;

public sealed record MinecraftLoaderInstallRequest(string Root, string Game, string InstanceId,
    InstallLoader Loader, string Build, JsonObject VanillaJson)
{
    public LocalJarArtifact? LocalInstaller { get; init; }
}

public interface IMinecraftLoaderInstaller
{
    Task<JsonObject> InstallAsync(MinecraftLoaderInstallRequest request, IProgress<string>? progress, CancellationToken token);
}
