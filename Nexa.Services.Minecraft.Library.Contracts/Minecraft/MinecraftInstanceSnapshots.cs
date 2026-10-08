namespace Nexa.Services.Minecraft;

/// <summary>Last-known display data. Authoritative discovery must finish before using it to launch.</summary>
public sealed record MinecraftInstanceDiscoverySnapshot(string RootDirectory, DateTimeOffset CapturedAt,
    IReadOnlyList<MinecraftInstanceDescriptor> Instances);

public interface IMinecraftInstanceSnapshotSource : IMinecraftInstanceSource
{
    ValueTask<MinecraftInstanceDiscoverySnapshot?> LoadSnapshotAsync(string minecraftRootDirectory,
        CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> RefreshAsync(string minecraftRootDirectory,
        CancellationToken cancellationToken = default);
}
