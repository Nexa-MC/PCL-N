

using Nexa.Services.Minecraft.Libraries;

namespace Nexa.Services.Minecraft;

/// <summary>
/// The platform and feature facts used by Mojang's ordered manifest rules.
/// Keeping this context independent of a particular manifest surface lets launch arguments,
/// libraries, and future rule-bearing sections share exactly the same matching semantics.
/// </summary>
public readonly record struct MinecraftRuleContext(
    MinecraftLibraryOperatingSystem OperatingSystem,
    string OperatingSystemVersion,
    bool Is64BitArchitecture,
    bool IsArm64Architecture,
    IReadOnlyDictionary<string, bool> Features)
{
    public string OperatingSystemName => OperatingSystem switch
    {
        MinecraftLibraryOperatingSystem.Win32 => "windows",
        MinecraftLibraryOperatingSystem.Linux => "linux",
        MinecraftLibraryOperatingSystem.MacOs => "osx",
        _ => "unknown",
    };

    public string ArchitectureName => IsArm64Architecture
        ? "arm64"
        : Is64BitArchitecture ? "x86_64" : "x86";

    public static MinecraftRuleContext From(
        MinecraftLibraryOperatingSystem operatingSystem,
        string? operatingSystemVersion,
        bool is64BitArchitecture,
        bool isArm64Architecture,
        IReadOnlyDictionary<string, bool>? features = null) =>
        new(
            operatingSystem,
            operatingSystemVersion ?? string.Empty,
            is64BitArchitecture,
            isArm64Architecture,
            features ?? EmptyFeatures.Instance);

    private sealed class EmptyFeatures : IReadOnlyDictionary<string, bool>
    {
        public static readonly EmptyFeatures Instance = new();
        public IEnumerable<string> Keys => [];
        public IEnumerable<bool> Values => [];
        public int Count => 0;
        public bool this[string key] => false;
        public bool ContainsKey(string key) => false;
        public IEnumerator<KeyValuePair<string, bool>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, bool>>().GetEnumerator();
        public bool TryGetValue(string key, out bool value) { value = false; return false; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
