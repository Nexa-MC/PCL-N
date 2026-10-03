using System.Diagnostics;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Libraries;
using Nexa.Services.Minecraft.ModLoaders;

namespace Nexa.Services.Minecraft.Launch;

public enum MinecraftLaunchIdentityMode
{
    Offline,
    Microsoft,
    ThirdParty,
}

/// <summary>The resolved identity a launch pipeline runs the game with.</summary>
public sealed record MinecraftLaunchIdentity(
    string PlayerName,
    string PlayerUuid,
    string AccessToken,
    MinecraftLaunchIdentityMode Mode)
{
    public string? AuthServer { get; init; }
}

public sealed record MinecraftLaunchRequest
{
    public required JsonObject VersionJson { get; init; }
    public IReadOnlyList<JsonObject> InheritedVersionJsons { get; init; } = [];
    public required string VersionId { get; init; }
    public required string InstanceDirectory { get; init; }
    public required string MinecraftRootDirectory { get; init; }
    public required string PlayerName { get; init; }
    public required string PlayerUuid { get; init; }
    public string AccessToken { get; init; } = "0";
    public string ClientId { get; init; } = string.Empty;
    public string AuthXuid { get; init; } = string.Empty;
    public string UserProperties { get; init; } = "{}";
    public string JavaExecutablePath { get; init; } = "java";
    public int JavaMajorVersion { get; init; } = 17;
    public int MemoryMegabytes { get; init; } = 2048;
    public int Width { get; init; } = 854;
    public int Height { get; init; } = 480;
    public bool Fullscreen { get; init; }
    public ProcessPriorityClass? ProcessPriority { get; init; }
    public bool IsolatedGameDirectory { get; init; }
    public string? CustomJvmArguments { get; init; }
    public string? CustomGameArguments { get; init; }
    public IReadOnlyList<string> ClasspathHeadEntries { get; init; } = [];

    /// <summary>
    /// An optional explicit client/version JAR override. When omitted the planner resolves the
    /// version's inheritance chain and selects the first installed client JAR, requiring the
    /// resolved artifact to exist before a launch plan is returned.
    /// </summary>
    public string? ClientJarPath { get; init; }
    public string? AuthlibInjectorPath { get; init; }
    public string? AuthlibServer { get; init; }
    public string? AuthlibPrefetchedMetadata { get; init; }
    public MinecraftLaunchIdentityMode IdentityMode { get; init; }
    public string? Server { get; init; }
    public string? WorldName { get; init; }
    public DateTimeOffset? ReleaseTime { get; init; }
    public string? NativesDirectory { get; init; }
    public string LauncherName { get; init; } = "NexaCL";
    public string LauncherVersion { get; init; } = "2.0.0";
    public string VersionType { get; init; } = "NexaCL";
    public bool UseSystemGlfw { get; init; }
    public bool HasCleanroom { get; init; }
    public MinecraftLibraryOperatingSystem OperatingSystem { get; init; } = MinecraftLibraryOperatingSystem.Unknown;
    public string OperatingSystemVersion { get; init; } = string.Empty;
    public bool Is64BitArchitecture { get; init; } = true;
    public bool IsArm64Architecture { get; init; }
    public IReadOnlyDictionary<string, bool> Features { get; init; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Immutable token vocabulary for one launch. Unknown placeholders are deliberately preserved so
/// the final plan validation can reject them instead of silently dropping a future Mojang token.
/// </summary>
public sealed class MinecraftLaunchTokenContext
{
    private readonly IReadOnlyDictionary<string, string> _values;

    private MinecraftLaunchTokenContext(IReadOnlyDictionary<string, string> values)
    {
        _values = values;
    }

    public static MinecraftLaunchTokenContext Create(
        MinecraftLaunchRequest request,
        string gameDirectory,
        string assetsRoot,
        string assetsIndex,
        string nativesDirectory,
        string classpathSeparator,
        string libraryDirectory,
        string classpath)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new MinecraftLaunchTokenContext(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = request.PlayerName,
            ["version_name"] = request.VersionId,
            ["game_directory"] = gameDirectory,
            ["assets_root"] = assetsRoot,
            ["assets_index_name"] = assetsIndex,
            ["auth_uuid"] = request.PlayerUuid,
            ["auth_access_token"] = request.AccessToken,
            ["auth_xuid"] = request.AuthXuid,
            ["clientid"] = request.ClientId,
            ["user_properties"] = request.UserProperties,
            ["user_type"] = request.IdentityMode switch { MinecraftLaunchIdentityMode.Offline => "legacy", MinecraftLaunchIdentityMode.ThirdParty => "mojang", _ => "msa" },
            ["version_type"] = request.VersionType,
            ["resolution_width"] = request.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["resolution_height"] = request.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["launcher_name"] = request.LauncherName,
            ["launcher_version"] = request.LauncherVersion,
            ["natives_directory"] = nativesDirectory,
            ["classpath_separator"] = classpathSeparator,
            ["library_directory"] = libraryDirectory,
            ["classpath"] = classpath,
        });
    }

    /// <summary>Replaces known <c>${name}</c> tokens while retaining unknown tokens verbatim.</summary>
    public string Replace(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains("${", StringComparison.Ordinal)) return value;

        System.Text.StringBuilder result = new(value.Length);
        int cursor = 0;
        while (cursor < value.Length)
        {
            int start = value.IndexOf("${", cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(value, cursor, value.Length - cursor);
                break;
            }

            result.Append(value, cursor, start - cursor);
            int end = value.IndexOf('}', start + 2);
            if (end < 0)
            {
                result.Append(value, start, value.Length - start);
                break;
            }

            string name = value[(start + 2)..end];
            if (_values.TryGetValue(name, out string? replacement)) result.Append(replacement);
            else result.Append(value, start, end - start + 1);
            cursor = end + 1;
        }

        return result.ToString();
    }
}

public sealed record MinecraftLaunchPlan(
    string JavaExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> ClasspathEntries,
    IReadOnlyList<MinecraftLibraryToken> Libraries,
    MinecraftModLoaderDescriptor ModLoader)
{
    /// <summary>Planner-confirmed boundary; null for legacy hand-built subprocess plans.</summary>
    public int? MainClassIndex { get; init; }

    /// <summary>Root-qualified instance identity, independent of the JVM working directory.</summary>
    public string InstanceDirectory { get; init; } = WorkingDirectory;
    public string GameDirectory { get; init; } = WorkingDirectory;
    public string? MinecraftRootDirectory { get; init; }
    /// <summary>The directory where native libraries must be extracted before launch.</summary>
    public string NativesDirectory { get; init; } = string.Empty;

    /// <summary>The resolved client/version JAR that was inserted at the classpath head.</summary>
    public string ClientJarPath { get; init; } = string.Empty;

    /// <summary>Major version from the selected runtime metadata. Observation code consumes
    /// this value directly and never guesses a version from the executable path.</summary>
    public int JavaMajorVersion { get; init; }
    /// <summary>Captured next-launch preference. Null preserves OS defaults for hand-built plans.</summary>
    public ProcessPriorityClass? ProcessPriority { get; init; }

    /// <summary>Configured heap request; -1 when custom arguments override the planner's value.</summary>
    public int HeapLimitMiB { get; init; } = -1;

    /// <summary>True when the classpath head came from an inheritsFrom/base version.</summary>
    public bool IsInheritedClientJar { get; init; }

    /// <summary>Native archives that must be staged before the process starts.</summary>
    public IReadOnlyList<MinecraftLibraryToken> NativeLibraries => Libraries.Where(static library => library.IsNatives).ToArray();

    public ProcessStartInfo ToStartInfo()
    {
        ProcessStartInfo startInfo = new(JavaExecutablePath)
        {
            WorkingDirectory = WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in Arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}
