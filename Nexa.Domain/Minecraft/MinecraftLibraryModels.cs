
using System.Text.Json.Nodes;


namespace Nexa.Services.Minecraft.Libraries;

public enum MinecraftLibraryOperatingSystem
{
    Win32,
    Linux,
    MacOs,
    Unknown,
}

public sealed record MinecraftLibraryResolutionRequest
{
    public required JsonObject VersionJson { get; init; }
    public required string MinecraftRootDirectory { get; init; }
    public string? TargetInstanceDirectory { get; init; }
    public required MinecraftLibraryOperatingSystem OperatingSystem { get; init; }
    public bool Is64BitArchitecture { get; init; }
    public bool IsArm64Architecture { get; init; }
    public string OperatingSystemVersion { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, bool> Features { get; init; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    public bool UseSystemGlfw { get; init; }
}

public sealed record MinecraftLibraryToken
{
    public string? OriginalName { get; init; }
    public string? NameWithoutVersion { get; init; }
    public string? Url { get; init; }
    public required string LocalPath { get; init; }
    public string? Sha1 { get; init; }
    public long Size { get; init; }
    public bool IsNatives { get; init; }
    public bool IsLocal { get; init; }
}

public readonly record struct MinecraftLibraryNameFragment(string Value)
{
    public bool Matches(string? coordinate) => coordinate?.Contains(Value, StringComparison.OrdinalIgnoreCase) == true;
}

public sealed record MinecraftClasspathPlanRequest
{
    public required IReadOnlyList<MinecraftLibraryToken> Libraries { get; init; }
    public IReadOnlyList<string> ClasspathHeadEntries { get; init; } = [];
    public IReadOnlyList<string> BundledClasspathEntries { get; init; } = [];
    public bool HasCleanroom { get; init; }
}

public sealed record MinecraftClasspathPlan(IReadOnlyList<string> Entries);

public static class MinecraftClasspathRuleRegistry
{
    private static readonly MinecraftLibraryNameFragment[] CleanroomExclusions =
    [
        new("org.lwjgl.lwjgl:lwjgl:2.9.4"),
        new("net.java.dev.jna:platform:3.4.0"),
        new("com.ibm.icu:icu4j-core-mojang:51.2"),
    ];

    public static IReadOnlyList<MinecraftLibraryNameFragment> CleanroomExcludedLibraryFragments => CleanroomExclusions;
}
