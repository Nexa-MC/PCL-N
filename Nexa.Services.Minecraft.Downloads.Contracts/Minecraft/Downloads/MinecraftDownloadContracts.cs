
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Assets;

namespace Nexa.Services.Minecraft.Downloads;

public sealed record MinecraftAssetFileState(bool Exists, long Length);
public sealed record MinecraftAssetDownloadFile(string Url, string LocalPath, string Hash, long ActualSize = -1);
public sealed record MinecraftAssetDownloadPlan(IReadOnlyList<MinecraftAssetDownloadFile> Files);

public sealed record MinecraftAssetDownloadPlanRequest
{
    public required IReadOnlyList<MinecraftAssetToken> Assets { get; init; }
    public bool CheckHash { get; init; }
    public IReadOnlyDictionary<string, MinecraftAssetFileState> ExistingFiles { get; init; } = new Dictionary<string, MinecraftAssetFileState>(StringComparer.Ordinal);
}

public enum MinecraftClientDownloadFailureReason
{
    None,
    NoClientJarDownloadInfo,
}

public sealed record MinecraftClientJarDownloadFile
{
    public required string Url { get; init; }
    public required string LocalPath { get; init; }
    public long MinimumSize { get; init; }
    public long ActualSize { get; init; } = -1;
    public string? Sha1 { get; init; }
}

public sealed record MinecraftClientJarDownloadPlan(MinecraftClientJarDownloadFile? File, MinecraftClientDownloadFailureReason FailureReason);

public sealed record MinecraftClientJarDownloadPlanRequest
{
    public required JsonObject VersionJson { get; init; }
    public required string InstanceDirectory { get; init; }
    public required string VersionName { get; init; }
}

public sealed record MinecraftAssetIndexDownloadPlan
{
    public string? IndexId { get; init; }
    public string? Url { get; init; }
    public string? LocalPath { get; init; }
    public bool UsedLegacyFallback { get; init; }
    public bool HasDownload => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(LocalPath);
}

public sealed record MinecraftAssetIndexDownloadPlanRequest
{
    public required JsonObject VersionJson { get; init; }
    public IReadOnlyList<JsonObject> InheritedVersionJsons { get; init; } = [];
    public required string MinecraftRootDirectory { get; init; }
    public bool UseLegacyFallback { get; init; } = true;
    public bool AllowUrlOnlyAssetIndex { get; init; } = true;
}
