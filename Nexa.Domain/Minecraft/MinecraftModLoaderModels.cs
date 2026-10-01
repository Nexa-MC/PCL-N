

namespace Nexa.Services.Minecraft.ModLoaders;

public enum MinecraftModLoaderKind
{
    Vanilla,
    OptiFine,
    Forge,
    NeoForge,
    Fabric,
    Quilt,
    LiteLoader,
    Cleanroom,
    LabyMod,
    Unknown,
}

public sealed record MinecraftModLoaderDescriptor(
    MinecraftModLoaderKind Kind,
    string? Version,
    string? MainClass,
    IReadOnlyList<string> Signals);
