using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexa.Services.Minecraft.Java;

public static class JavaRuntimePackagePlanner
{
    private static readonly HashSet<string> IgnoredSha1 = ["12976a6c2b227cbac58969c1455444596c894656", "c80e4bab46e34d02826eab226a4441d0970f2aba", "84d2102ad171863db04e7ee22a259d1f6c5de4a5"];

    public static JavaRuntimePackageDescriptor SelectPackage(string runtimeIndexJson, JavaRuntimePlatform platform, string requestedComponent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIndexJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedComponent);
        using JsonDocument document = JsonDocument.Parse(runtimeIndexJson);
        if (!document.RootElement.TryGetProperty(platform.ToMojangKey(), out JsonElement platformElement)) throw new InvalidOperationException($"Mojang did not publish a Java runtime for {platform.ToMojangKey()}.");
        if (platformElement.TryGetProperty(requestedComponent, out JsonElement exact)) return CreateDescriptor(requestedComponent, FirstVersion(exact));
        foreach (JsonProperty component in platformElement.EnumerateObject())
        {
            try
            {
                JsonElement first = FirstVersion(component.Value);
                string version = Required(first, "version", "name");
                if (version.StartsWith(requestedComponent, StringComparison.OrdinalIgnoreCase)) return CreateDescriptor(component.Name, first);
            }
            catch (InvalidOperationException)
            {
                // Ignore malformed catalog entries while still allowing a valid sibling to be selected.
            }
        }

        throw new InvalidOperationException($"No Java runtime component matches {requestedComponent}.");
    }

    public static JavaRuntimeDownloadPlan CreateDownloadPlan(JavaRuntimePackageDescriptor packageDescriptor, string manifestJson, string runtimeRootDirectory)
    {
        ArgumentNullException.ThrowIfNull(packageDescriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRootDirectory);
        string runtimeRoot = Path.GetFullPath(runtimeRootDirectory);
        string targetDirectory = ResolveComponentDirectory(runtimeRoot, packageDescriptor.ComponentName);
        using JsonDocument document = JsonDocument.Parse(manifestJson);
        if (!document.RootElement.TryGetProperty("files", out JsonElement filesElement) || filesElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Java runtime manifest does not contain files.");
        List<JavaRuntimeDownloadFile> files = [];
        foreach (JsonProperty property in filesElement.EnumerateObject())
        {
            if (!property.Value.TryGetProperty("downloads", out JsonElement downloads) || !downloads.TryGetProperty("raw", out JsonElement raw)) continue;
            string url = Required(raw, "url");
            string sha1 = Required(raw, "sha1");
            long size = raw.GetProperty("size").GetInt64();
            if (IgnoredSha1.Contains(sha1)) continue;
            string target = ResolveContained(targetDirectory, property.Name);
            bool executable = property.Value.TryGetProperty("executable", out JsonElement executableElement) && executableElement.ValueKind == JsonValueKind.True;
            files.Add(new JavaRuntimeDownloadFile(property.Name, target, url, sha1, size, executable));
        }

        return new JavaRuntimeDownloadPlan(packageDescriptor.ComponentName, packageDescriptor.VersionName, packageDescriptor.ManifestUrl, targetDirectory, files);
    }

    private static JsonElement FirstVersion(JsonElement component) => component.ValueKind == JsonValueKind.Array && component.GetArrayLength() > 0 ? component[0] : throw new InvalidOperationException("Java runtime component has no versions.");
    private static JavaRuntimePackageDescriptor CreateDescriptor(string component, JsonElement version) => new(component, Required(version, "version", "name"), Required(version, "manifest", "url"));
    private static string Required(JsonElement element, string property, string? nested = null)
    {
        JsonElement value = nested is null ? element.GetProperty(property) : element.GetProperty(property).GetProperty(nested);
        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? throw new InvalidOperationException($"Java runtime manifest field '{property}' is empty.") : text;
    }
    private static string ResolveContained(string root, string relative)
    {
        string normalized = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Split(Path.DirectorySeparatorChar).Any(static segment => segment is "" or "." or "..")) throw new InvalidOperationException("Java runtime file escapes its target directory.");
        string target = Path.GetFullPath(Path.Combine(root, normalized));
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Java runtime file escapes its target directory.");
        return target;
    }

    private static string ResolveComponentDirectory(string runtimeRoot, string componentName)
    {
        if (string.IsNullOrWhiteSpace(componentName) || Path.IsPathRooted(componentName) || componentName.Split(['/', '\\'], StringSplitOptions.None).Any(static segment => segment is "" or "." or ".."))
            throw new InvalidOperationException("Java runtime component has an unsafe name.");
        return ResolveContained(runtimeRoot, componentName);
    }
}

public sealed class JavaRuntimeDownloadPlanService(IJavaRuntimeMetadataProvider metadataProvider)
{
    private readonly IJavaRuntimeMetadataProvider _metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));

    public async ValueTask<JavaRuntimeDownloadPlan> CreatePlanAsync(string requestedComponent, JavaRuntimePlatform platform, string runtimeRootDirectory, CancellationToken cancellationToken = default)
    {
        string index = await _metadataProvider.GetRuntimeIndexAsync(cancellationToken).ConfigureAwait(false);
        JavaRuntimePackageDescriptor packageDescriptor = JavaRuntimePackagePlanner.SelectPackage(index, platform, requestedComponent);
        string manifest = await _metadataProvider.GetManifestAsync(packageDescriptor.ManifestUrl, cancellationToken).ConfigureAwait(false);
        return JavaRuntimePackagePlanner.CreateDownloadPlan(packageDescriptor, manifest, runtimeRootDirectory);
    }

    /// <summary>Returns the launcher-scoped runtime directory without coupling Services to a platform project.</summary>
    public ValueTask<JavaRuntimeDownloadPlan> CreatePlanAsync(
        string requestedComponent,
        JavaRuntimePlatform platform,
        IJavaRuntimePathProvider pathProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        return CreatePlanAsync(
            requestedComponent,
            platform,
            GetDefaultRuntimeRoot(pathProvider.ApplicationDataDirectory),
            cancellationToken);
    }

    public static string GetDefaultRuntimeRoot(string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        return Path.Combine(Path.GetFullPath(applicationDataDirectory), ".minecraft", "runtime");
    }
}

public static class MinecraftJavaRequirementResolver
{
    private static readonly DateTimeOffset ManifestJava21Boundary = new(2024, 4, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ManifestJava25Boundary = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly MinecraftGameVersion CalendarJava25Boundary = new(MinecraftVersionScheme.Calendar, 26, 1, 0);
    private static readonly MinecraftGameVersion LegacyJava8Maximum = new(MinecraftVersionScheme.Legacy, 1, 16, 5);
    private static readonly MinecraftGameVersion LegacyJava17Boundary = new(MinecraftVersionScheme.Legacy, 1, 18, 0);
    private static readonly MinecraftGameVersion LegacyJava21Boundary = new(MinecraftVersionScheme.Legacy, 1, 20, 5);

    /// <summary>
    /// Normalizes a parsed Minecraft version at the compatibility boundary. Historical
    /// shorthand such as "20.5" becomes Version(1,20,5), while calendar "26.1" remains
    /// Version(26,1,0) and is never mistaken for a 1.x release.
    /// </summary>
    public static Version NormalizeVanilla(Version vanilla)
        => MinecraftGameVersion.FromVersion(vanilla).ToVersion();

    public static JavaRequirementResolution Resolve(MinecraftJavaRequirementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Mojang's javaVersion is the authoritative runtime contract. Apply the historical
        // Minecraft-era fallback only when that metadata is absent, then intersect loader
        // constraints. This keeps a valid calendar 26.1/Java 25 manifest from conflicting
        // with a stale 1.x inference.
        JavaVersionRange range;
        string? component = null;
        MinecraftGameVersion? version = GetEffectiveMinecraftVersion(request);
        if (request.ManifestJavaMajorVersion is int manifestMajor)
        {
            if (manifestMajor < 7)
                return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.InvalidVersionMetadata, "Manifest Java major version is below 7.");
            range = JavaVersionRange.ForMajor(manifestMajor);
            component = request.ManifestJavaComponent;
        }
        else if (version is { } minecraftVersion)
        {
            range = GetVanillaFallback(minecraftVersion);
        }
        else if (request.ReleaseTime is { } release && release >= ManifestJava25Boundary)
        {
            range = JavaVersionRange.ForMajor(25);
            component = request.ManifestJavaComponent;
        }
        else if (request.ReleaseTime is { } snapshot && snapshot >= ManifestJava21Boundary)
        {
            range = JavaVersionRange.ForMajor(21);
            component = request.ManifestJavaComponent;
        }
        else
        {
            range = JavaVersionRange.Any;
        }

        if (request.HasCleanroom)
        {
            if (!TryParseLoaderVersion(request.CleanroomVersion, out Version? cleanroom))
            {
                return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.InvalidVersionMetadata, "Cleanroom version metadata is invalid.");
            }

            // Cleanroom uses a 0.x version line; 0.5.0 and later require Java 25, while
            // pre-0.5 builds require Java 21. Compare the loader's own coordinate rather than
            // looking only at the major component (which would misclassify 0.5.1 as Java 21).
            JavaVersionRange cleanroomRange = cleanroom! >= new Version(0, 5)
                ? JavaVersionRange.ForMajor(25)
                : JavaVersionRange.ForMajor(21);
            if (!TryIntersect(ref range, cleanroomRange, out JavaRequirementResolution? failure))
                return failure!;
        }

        if (version is { } minecraft)
        {
            if (request.HasForge && minecraft < new MinecraftGameVersion(MinecraftVersionScheme.Legacy, 1, 12, 0) && IsLegacyForge(request.ForgeVersion))
            {
                if (!TryIntersect(ref range, JavaVersionRange.ForMajor(7), out JavaRequirementResolution? failure))
                    return failure!;
            }

            if (request.HasOptiFine && minecraft >= new MinecraftGameVersion(MinecraftVersionScheme.Legacy, 1, 8, 0) && minecraft < new MinecraftGameVersion(MinecraftVersionScheme.Legacy, 1, 13, 0))
            {
                if (!TryIntersect(ref range, JavaVersionRange.ForMajor(8), out JavaRequirementResolution? failure))
                    return failure!;
            }

            if (request.HasLabyMod && minecraft < new MinecraftGameVersion(MinecraftVersionScheme.Legacy, 1, 13, 0))
            {
                if (!TryIntersect(ref range, JavaVersionRange.ForMajor(8), out JavaRequirementResolution? failure))
                    return failure!;
            }
        }

        if (request.HasLiteLoader)
        {
            if (!TryIntersect(ref range, new JavaVersionRange(new Version(1, 8), JavaVersionRange.Java8Maximum), out JavaRequirementResolution? failure))
                return failure!;
        }

        if (range.Minimum > range.Maximum)
            return JavaRequirementResolution.Invalid(JavaRequirementFailureReason.ConflictingRequirements, "Minecraft metadata contains incompatible Java requirements.");
        return JavaRequirementResolution.Valid(range, component);
    }

    private static MinecraftGameVersion? GetEffectiveMinecraftVersion(MinecraftJavaRequirementRequest request)
    {
        if (request.MinecraftVersion is { } typed) return typed;
        if (!request.HasReliableVanillaVersion) return null;
        return request.VanillaVersion is { } raw ? MinecraftGameVersion.FromVersion(raw) : null;
    }

    private static JavaVersionRange GetVanillaFallback(MinecraftGameVersion version)
    {
        if (version.IsCalendar)
            return version >= CalendarJava25Boundary ? JavaVersionRange.ForMajor(25) : JavaVersionRange.Any;
        if (version <= LegacyJava8Maximum)
            return JavaVersionRange.ForMajor(8);
        if (version < LegacyJava17Boundary)
            return JavaVersionRange.ForMajor(16);
        if (version < LegacyJava21Boundary)
            return JavaVersionRange.ForMajor(17);
        return JavaVersionRange.ForMajor(21);
    }

    private static bool TryIntersect(ref JavaVersionRange current, JavaVersionRange required, out JavaRequirementResolution? failure)
    {
        if (current.TryIntersect(required, out JavaVersionRange result))
        {
            current = result;
            failure = null;
            return true;
        }

        failure = JavaRequirementResolution.Invalid(JavaRequirementFailureReason.ConflictingRequirements, "Overlapping Java version requirements are disjoint.");
        return false;
    }

    private static bool IsLegacyForge(string? value) => string.IsNullOrWhiteSpace(value) || value.StartsWith("9.", StringComparison.Ordinal) || value.StartsWith("10.", StringComparison.Ordinal);

    private static bool TryParseLoaderVersion(string? value, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized = value.Split('-', 2)[0];
        return Version.TryParse(normalized, out version);
    }
}

public sealed class JavaSelectionService(IJavaRuntimeLocator locator)
{
    private readonly IJavaRuntimeLocator _locator = locator ?? throw new ArgumentNullException(nameof(locator));

    public async ValueTask<JavaSelectionResult> SelectAsync(MinecraftJavaRequirementRequest request, CancellationToken cancellationToken = default)
        => await SelectAsync(request, new AutoSelectJavaPreference(), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Selects against the resolved Java contract while honoring an explicit per-instance Java
    /// executable. An explicit executable is inspected and either accepted as compatible or
    /// rejected; it never silently falls through to a different runtime.
    /// </summary>
    public async ValueTask<JavaSelectionResult> SelectAsync(
        MinecraftJavaRequirementRequest request,
        JavaPreference preference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preference);
        JavaRequirementResolution requirement = MinecraftJavaRequirementResolver.Resolve(request);
        return await SelectAsync(requirement, preference, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<JavaSelectionResult> SelectAsync(JavaRequirementResolution requirement, JavaPreference preference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(preference);
        if (!requirement.Success)
            return new JavaSelectionResult { Success = false, Requirement = requirement, FailureReason = JavaSelectionFailureReason.InvalidVersionMetadata, Detail = requirement.Detail };

        IReadOnlyList<JavaRuntimeCandidate> candidates;
        try
        {
            if (preference is ExistingJavaPreference existing)
            {
                JavaRuntimeCandidate? inspected = await _locator
                    .InspectAsync(existing.JavaExecutablePath, cancellationToken)
                    .ConfigureAwait(false);
                candidates = inspected is null ? [] : [inspected];
            }
            else
            {
                candidates = await _locator.FindAllAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new JavaSelectionResult { Success = false, Requirement = requirement, FailureReason = JavaSelectionFailureReason.LocatorUnavailable, Detail = exception.Message };
        }

        JavaRuntimeCandidate? selected = candidates
            .Where(candidate => candidate.IsEnabled && candidate.IsAvailable && requirement.Range.Contains(candidate.Installation.Version))
            .OrderBy(candidate => candidate.Installation.MajorVersion)
            .ThenBy(candidate => candidate.Installation.IsJre ? 1 : 0)
            .ThenBy(candidate => BrandRank(candidate.Installation.Brand))
            .ThenBy(candidate => candidate.Installation.Version)
            .ThenBy(candidate => candidate.Installation.JavaHome, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return selected is null
            ? new JavaSelectionResult { Success = false, Requirement = requirement, FailureReason = JavaSelectionFailureReason.NoCompatibleRuntime, Detail = "No enabled Java runtime satisfies the Minecraft requirement.", SuggestedDownloadComponent = requirement.RecommendedComponent }
            : new JavaSelectionResult { Success = true, Requirement = requirement, SelectedJava = selected, SuggestedDownloadComponent = requirement.RecommendedComponent };
    }

    private static int BrandRank(JavaBrand brand) => brand switch
    {
        JavaBrand.EclipseTemurin => 0,
        JavaBrand.Microsoft => 1,
        JavaBrand.Zulu => 2,
        _ => 10,
    };
}

public static class JavaRuntimeAcquisitionPlanner
{
    public static JavaRuntimeAcquisitionDecision Plan(JavaRequirementResolution requirement, bool hasForge = false) =>
        Plan(requirement.Range, requirement.RecommendedComponent, hasForge);

    public static JavaRuntimeAcquisitionDecision Plan(JavaVersionRange range, string? recommendedComponent = null, bool hasForge = false)
    {
        if (range.Maximum <= JavaVersionRange.Java7Maximum)
            return new JavaRuntimeAcquisitionDecision { CanAutoDownload = false, BlockReason = hasForge ? JavaAcquisitionBlockReason.LegacyForgeNeedsFixerOrJava7 : JavaAcquisitionBlockReason.LegacyJava7Required };
        if (range.Minimum >= new Version(1, 8, 0, 141) && range.Minimum <= JavaVersionRange.Java8Maximum && range.Maximum < JavaVersionRange.Java8Maximum)
            return new JavaRuntimeAcquisitionDecision { CanAutoDownload = false, BlockReason = JavaAcquisitionBlockReason.Java8Update141To320Required };
        if (range.Minimum >= new Version(1, 8, 0, 141) && range.Maximum == JavaVersionRange.Java8Maximum)
            return new JavaRuntimeAcquisitionDecision { CanAutoDownload = false, BlockReason = JavaAcquisitionBlockReason.Java8Update141OrLaterRequired };
        int major = range.Minimum.Major == 1 ? range.Minimum.Minor : range.Minimum.Major;
        return new JavaRuntimeAcquisitionDecision { CanAutoDownload = true, JavaVersionCode = major.ToString(CultureInfo.InvariantCulture), DownloadComponent = string.IsNullOrWhiteSpace(recommendedComponent) ? major.ToString(CultureInfo.InvariantCulture) : recommendedComponent };
    }
}

public static class JavaPreferenceParser
{
    public const string LegacyUseGlobalText = "使用全局设置";

    public static JavaPreference Parse(string? rawPreference, string? relativePathBaseDirectory = null)
    {
        JavaPreference preference = TryParseJson(rawPreference) ?? ParseLegacy(rawPreference);
        return Normalize(preference, relativePathBaseDirectory);
    }

    private static JavaPreference? TryParseJson(string? rawPreference)
    {
        if (string.IsNullOrWhiteSpace(rawPreference) || !rawPreference.TrimStart().StartsWith('{')) return null;
        try
        {
            JsonObject? json = JsonNode.Parse(rawPreference)?.AsObject();
            string? kind = json?["kind"]?.ToString();
            if (kind is null) return null;
            return kind.ToLowerInvariant() switch
            {
                "auto" => new AutoSelectJavaPreference(),
                "global" => new UseGlobalJavaPreference(),
                "exist" => ReadString(json, "JavaExePath") is { } path ? new ExistingJavaPreference(path) : null,
                "relative" => ReadString(json, "RelativePath") is { } relativePath ? new UseRelativeJavaPreference(relativePath) : null,
                _ => null,
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static JavaPreference ParseLegacy(string? rawPreference)
    {
        string? trimmed = rawPreference?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return new AutoSelectJavaPreference();
        return string.Equals(trimmed, LegacyUseGlobalText, StringComparison.Ordinal) ? new UseGlobalJavaPreference() : new ExistingJavaPreference(trimmed);
    }

    private static JavaPreference Normalize(JavaPreference preference, string? relativePathBaseDirectory) => preference switch
    {
        ExistingJavaPreference existing when !Path.IsPathRooted(existing.JavaExecutablePath) => new UseGlobalJavaPreference(),
        UseRelativeJavaPreference relative when !IsSafeRelativePath(relative.RelativePath, relativePathBaseDirectory) => new UseGlobalJavaPreference(),
        _ => preference,
    };

    private static bool IsSafeRelativePath(string relativePath, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(baseDirectory) || Path.IsPathRooted(relativePath)) return false;
        try
        {
            string baseFullPath = EnsureTrailingSeparator(Path.GetFullPath(baseDirectory));
            string resolvedPath = Path.GetFullPath(Path.Combine(baseFullPath, relativePath));
            return resolvedPath.StartsWith(baseFullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string EnsureTrailingSeparator(string directory) => directory.EndsWith(Path.DirectorySeparatorChar) || directory.EndsWith(Path.AltDirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;

    private static string? ReadString(JsonObject? json, string propertyName)
    {
        string? value = json?[propertyName]?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
