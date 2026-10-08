using System.Collections.ObjectModel;

namespace Nexa.Services.Minecraft.Launch;

public static partial class MinecraftLaunchHooks
{
    public static IReadOnlyDictionary<string, string> ParseEnvironment(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumCommandLength || text.Contains('\0'))
            throw new ArgumentException("Environment settings exceed the bounded format.", nameof(text));
        Dictionary<string, string> values = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int separator = line.IndexOf('=');
            string key = separator < 0 ? "" : line[..separator];
            if (key.Length is 0 or > 128 || !(char.IsAsciiLetter(key[0]) || key[0] == '_')
                || key.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')
                || line.Contains('\r') || values.Count == 128 || !values.TryAdd(key, line[(separator + 1)..]))
                throw new ArgumentException("Use unique KEY=VALUE lines with ASCII variable names (at most 128).", nameof(text));
        }
        return new ReadOnlyDictionary<string, string>(values);
    }

    public static IReadOnlyList<string> ParseClasspathHead(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumCommandLength || text.Contains('\0'))
            throw new ArgumentException("Classpath settings exceed the bounded format.", nameof(text));
        List<string> paths = [];
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!Path.IsPathFullyQualified(line) || line.Contains('\r') || paths.Count == 128)
                throw new ArgumentException("Use one fully qualified classpath path per line (at most 128).", nameof(text));
            paths.Add(Path.GetFullPath(line));
        }
        return paths.AsReadOnly();
    }
}

/// <summary>Captured read-only source directories for one launch; sources are never mutated.</summary>
public sealed record MinecraftLaunchOverlay(bool SafeLaunch = false,
    string? ModsSource = null, string? ResourcePacksSource = null, string? ShaderPacksSource = null, string? ConfigSource = null)
{
    public bool IsRequired => SafeLaunch || ModsSource is not null || ResourcePacksSource is not null || ShaderPacksSource is not null || ConfigSource is not null;
}

public static class MinecraftSafeLaunchPolicy
{
    public static MinecraftLaunchRequest Apply(MinecraftLaunchRequest request) => !request.Overlay.SafeLaunch ? request : request with
    {
        CustomJvmArguments = null,
        CustomGameArguments = null,
        WrapperCommand = "",
        PreLaunchCommand = "",
        PostExitCommand = "",
        WaitForPreLaunchCommand = true,
        ClasspathHeadEntries = [],
        EnvironmentVariables = new Dictionary<string, string>(),
        UseSystemGlfw = false,
        GpuPreference = "auto",
        RendererPreference = "auto",
        Overlay = new(SafeLaunch: true),
    };
}
