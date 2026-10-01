
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Install;

public sealed record InstallBuildSelection(InstallLoader Loader, string Version);
public sealed record InstallEligibilityQuery(string GameVersion, IReadOnlyList<InstallBuildSelection> Selection,
    InstallLoader? PrimaryLoader = null, InstallLoader? Catalog = null, IReadOnlyList<string>? Candidates = null,
    string? ToggleVersion = null, IReadOnlyList<InstallBuildSelection>? InstalledSelection = null);
public sealed record InstallLoaderEligibility(InstallLoader Loader, bool IsAddon, bool Visible);
public sealed record InstallBuildEligibility(string? Conflict, string? Notice);
public sealed record InstallEligibilityResult(IReadOnlyList<InstallBuildSelection> Selection, InstallLoader? PrimaryLoader,
    IReadOnlyList<InstallLoaderEligibility> Loaders, IReadOnlyDictionary<string, InstallBuildEligibility> Builds,
    string? CommitError, string? Rejection);
public static class InstallEligibilityContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.install.eligibility");
}
