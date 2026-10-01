



namespace Nexa.Services.Minecraft.Java;

public enum JavaRuntimeOperatingSystem
{
    Win32,
    Linux,
    MacOs,
}

public enum JavaRuntimeArchitecture
{
    X86,
    X64,
    Arm64,
}

public readonly record struct JavaRuntimePlatform(JavaRuntimeOperatingSystem OperatingSystem, JavaRuntimeArchitecture Architecture)
{
    public string ToMojangKey() => OperatingSystem switch
    {
        JavaRuntimeOperatingSystem.Win32 => Architecture switch { JavaRuntimeArchitecture.X86 => "windows-x86", JavaRuntimeArchitecture.Arm64 => "windows-arm64", _ => "windows-x64" },
        JavaRuntimeOperatingSystem.Linux => Architecture == JavaRuntimeArchitecture.X86 ? "linux-i386" : "linux",
        JavaRuntimeOperatingSystem.MacOs => Architecture == JavaRuntimeArchitecture.Arm64 ? "mac-os-arm64" : "mac-os",
        _ => throw new ArgumentOutOfRangeException(nameof(OperatingSystem)),
    };
}

public sealed record JavaRuntimePackageDescriptor(string ComponentName, string VersionName, string ManifestUrl);
public sealed record JavaRuntimeDownloadFile(string RelativePath, string TargetPath, string Url, string Sha1, long Size, bool Executable = false);
public sealed record JavaRuntimeDownloadPlan(string ComponentName, string VersionName, string ManifestUrl, string TargetDirectory, IReadOnlyList<JavaRuntimeDownloadFile> Files);

public interface IJavaRuntimeMetadataProvider
{
    ValueTask<string> GetRuntimeIndexAsync(CancellationToken cancellationToken = default);
    ValueTask<string> GetManifestAsync(string manifestUrl, CancellationToken cancellationToken = default);
}

/// <summary>Minimal path seam owned by Services; platform adapters can implement it without a reverse reference.</summary>
public interface IJavaRuntimePathProvider
{
    string ApplicationDataDirectory { get; }
}

public enum JavaBrand
{
    EclipseTemurin,
    Liberica,
    Zulu,
    Corretto,
    Microsoft,
    IbmSemeru,
    Oracle,
    Dragonwell,
    TencentKona,
    OpenJdk,
    GraalVmCommunity,
    JetBrains,
    Unknown,
}

public enum JavaArchitecture
{
    Unknown,
    X86,
    X64,
    Arm,
    Arm64,
}

public enum JavaSource
{
    AutoScanned,
    AutoInstalled,
    ManualAdded,
}

public sealed record JavaInstallation
{
    public JavaInstallation(
        string javaHome,
        string javaExecutablePath,
        string? windowedJavaExecutablePath,
        Version version,
        JavaBrand brand,
        JavaArchitecture architecture,
        bool is64Bit,
        bool isJre)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(javaHome);
        ArgumentException.ThrowIfNullOrWhiteSpace(javaExecutablePath);
        ArgumentNullException.ThrowIfNull(version);
        JavaHome = Path.GetFullPath(javaHome);
        JavaExecutablePath = Path.GetFullPath(javaExecutablePath);
        WindowedJavaExecutablePath = string.IsNullOrWhiteSpace(windowedJavaExecutablePath) ? null : Path.GetFullPath(windowedJavaExecutablePath);
        Version = version;
        Brand = brand;
        Architecture = architecture;
        Is64Bit = is64Bit;
        IsJre = isJre;
    }

    public string JavaHome { get; }
    public string JavaExecutablePath { get; }
    public string? WindowedJavaExecutablePath { get; }
    public Version Version { get; }
    public JavaBrand Brand { get; }
    public JavaArchitecture Architecture { get; }
    public bool Is64Bit { get; }
    public bool IsJre { get; }
    public int MajorVersion => Version.Major == 1 ? Version.Minor : Version.Major;
    public override string ToString() => $"{(IsJre ? "JRE" : "JDK")} {Version} {Brand} {(Is64Bit ? "64 Bit" : "32 Bit")} | {JavaHome}";
}

public sealed record JavaRuntimeCandidate(
    JavaInstallation Installation,
    bool IsEnabled = true,
    bool IsAvailable = true,
    JavaSource Source = JavaSource.AutoScanned);

public interface IJavaRuntimeLocator
{
    /// <summary>Invalidates only this discovery lifetime after runtime changes.</summary>
    void Invalidate() { }

    ValueTask<IReadOnlyList<JavaRuntimeCandidate>> FindAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Inspects one explicitly selected executable without widening to other runtimes.</summary>
    ValueTask<JavaRuntimeCandidate?> InspectAsync(
        string javaExecutablePath,
        CancellationToken cancellationToken = default);
}

public abstract record JavaPreference;
public sealed record AutoSelectJavaPreference : JavaPreference;
public sealed record ExistingJavaPreference(string JavaExecutablePath) : JavaPreference;
public sealed record UseGlobalJavaPreference : JavaPreference;
public sealed record UseRelativeJavaPreference(string RelativePath) : JavaPreference;

public readonly record struct JavaVersionRange
{
    public static JavaVersionRange Any { get; } = new(new Version(1, 7), new Version(99, 0));
    public static Version Java7Maximum { get; } = new(1, 7, 0, 999);
    public static Version Java8Maximum { get; } = new(1, 8, 0, 999);

    public JavaVersionRange(Version minimum, Version maximum)
    {
        ArgumentNullException.ThrowIfNull(minimum);
        ArgumentNullException.ThrowIfNull(maximum);
        if (minimum > maximum) throw new ArgumentException("The Java minimum cannot exceed the maximum.");
        Minimum = minimum;
        Maximum = maximum;
    }

    public Version Minimum { get; }
    public Version Maximum { get; }

    public bool Contains(Version value) => value >= Minimum && value <= Maximum;
    public static JavaVersionRange ForMajor(int major) => major switch
    {
        7 => new JavaVersionRange(new Version(1, 7), Java7Maximum),
        8 => new JavaVersionRange(new Version(1, 8), Java8Maximum),
        _ => new JavaVersionRange(new Version(major, 0), new Version(major, 999, 999, 999)),
    };

    /// <summary>
    /// Mathematical intersection: minimum = max(both minimums), maximum = min(both maximums).
    /// Returns false for a disjoint range, which callers surface as conflicting requirements
    /// instead of silently widening.
    /// </summary>
    public bool TryIntersect(JavaVersionRange other, out JavaVersionRange result)
    {
        Version minimum = Minimum > other.Minimum ? Minimum : other.Minimum;
        Version maximum = Maximum < other.Maximum ? Maximum : other.Maximum;
        if (minimum > maximum)
        {
            result = Any;
            return false;
        }

        result = new JavaVersionRange(minimum, maximum);
        return true;
    }

    public JavaVersionRange Intersect(JavaVersionRange other) =>
        TryIntersect(other, out JavaVersionRange result) ? result : throw new ArgumentException("The ranges do not intersect.");
}

public sealed record MinecraftJavaRequirementRequest
{
    /// <summary>
    /// The typed Minecraft coordinate. New callers should prefer this over
    /// <see cref="VanillaVersion"/> so calendar versions cannot be confused with Java versions.
    /// </summary>
    public MinecraftGameVersion? MinecraftVersion { get; init; }

    /// <summary>
    /// Compatibility input for callers that still provide a parsed <see cref="Version"/>.
    /// It is interpreted as a Minecraft coordinate, never as a Java version.
    /// </summary>
    public Version? VanillaVersion { get; init; }
    public bool HasReliableVanillaVersion { get; init; }
    public DateTimeOffset? ReleaseTime { get; init; }
    public int? ManifestJavaMajorVersion { get; init; }
    public string? ManifestJavaComponent { get; init; }
    public bool HasOptiFine { get; init; }
    public bool HasForge { get; init; }
    public string? ForgeVersion { get; init; }
    public bool HasCleanroom { get; init; }
    public string? CleanroomVersion { get; init; }
    public bool HasFabric { get; init; }
    public bool HasLiteLoader { get; init; }
    public bool HasLabyMod { get; init; }
}

public enum JavaRequirementFailureReason
{
    None,
    InvalidVersionMetadata,
    ConflictingRequirements,
}

public sealed record JavaRequirementResolution
{
    public required bool Success { get; init; }
    public required JavaVersionRange Range { get; init; }
    public string? RecommendedComponent { get; init; }
    public JavaRequirementFailureReason FailureReason { get; init; }
    public string? Detail { get; init; }

    public static JavaRequirementResolution Valid(JavaVersionRange range, string? component = null) => new() { Success = true, Range = range, RecommendedComponent = component };
    public static JavaRequirementResolution Invalid(JavaRequirementFailureReason reason, string detail) => new() { Success = false, Range = JavaVersionRange.Any, FailureReason = reason, Detail = detail };
}

public enum JavaSelectionFailureReason
{
    None,
    InvalidVersionMetadata,
    NoCompatibleRuntime,
    LocatorUnavailable,
}

public sealed record JavaSelectionResult
{
    public required bool Success { get; init; }
    public required JavaRequirementResolution Requirement { get; init; }
    public JavaRuntimeCandidate? SelectedJava { get; init; }
    public JavaSelectionFailureReason FailureReason { get; init; }
    public string? Detail { get; init; }
    public string? SuggestedDownloadComponent { get; init; }
}

public enum JavaAcquisitionBlockReason
{
    None,
    LegacyJava7Required,
    LegacyForgeNeedsFixerOrJava7,
    Java8Update141To320Required,
    Java8Update141OrLaterRequired,
}

public sealed record JavaRuntimeAcquisitionDecision
{
    public required bool CanAutoDownload { get; init; }
    public string? JavaVersionCode { get; init; }
    public string? DownloadComponent { get; init; }
    public JavaAcquisitionBlockReason BlockReason { get; init; }
}
