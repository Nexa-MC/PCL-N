
using System.Text.Json.Nodes;

namespace Nexa.Services.Minecraft.Assets;

public sealed record MinecraftAssetToken
{
    public required string LocalPath { get; init; }
    public required string SourcePath { get; init; }
    public required string Hash { get; init; }
    public long Size { get; init; }
}

public sealed record MinecraftAssetIndexRequest
{
    public required JsonObject VersionJson { get; init; }
    public IReadOnlyList<JsonObject> InheritedVersionJsons { get; init; } = [];
    public bool UseLegacyFallback { get; init; }
    public bool AllowUrlOnlyAssetIndex { get; init; }
}

public sealed record MinecraftAssetIndexNameRequest
{
    public required JsonObject VersionJson { get; init; }
    public IReadOnlyList<JsonObject> InheritedVersionJsons { get; init; } = [];
}

public sealed record MinecraftAssetIndexResolution(JsonObject? IndexJson, bool UsedLegacyFallback);

public sealed record MinecraftAssetListRequest
{
    public required JsonObject IndexJson { get; init; }
    public required string MinecraftRootDirectory { get; init; }
    public required string InstanceDirectory { get; init; }
}
