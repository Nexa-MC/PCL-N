

namespace Nexa.Services.Minecraft;

/// <summary>A discovered, user-owned Minecraft instance rooted below a Minecraft installation.</summary>
public sealed record MinecraftInstanceDescriptor(
    string Id,
    string DirectoryPath,
    string VersionId,
    MinecraftVersionDescriptor Version,
    MinecraftInstanceMetadata Metadata)
{
    public Nexa.Core.Media.PngImage? Icon { get; init; }
    public string? MetadataError { get; init; }
}
