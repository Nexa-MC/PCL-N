
using System.Text.Json.Nodes;

namespace Nexa.Services.Minecraft.Launch;

/// <summary>Inputs for resolving the client JAR used by a launch.</summary>
public sealed record MinecraftClientJarResolutionRequest
{
    public required JsonObject VersionJson { get; init; }
    public IReadOnlyList<JsonObject> InheritedVersionJsons { get; init; } = [];
    public required string VersionId { get; init; }
    public required string InstanceDirectory { get; init; }
    public required string MinecraftRootDirectory { get; init; }
    public string? ExplicitClientJarPath { get; init; }
}

public sealed record MinecraftClientJarResolution(string Path, string VersionId, bool IsInherited);
