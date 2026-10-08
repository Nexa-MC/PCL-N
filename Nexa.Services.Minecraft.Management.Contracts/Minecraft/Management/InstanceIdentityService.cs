using Nexa.Core.Media;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceIdentityFields(string DisplayName, string Description, string IconPath, bool Starred,
    IReadOnlyList<string> Tags, string Group, string Notes, string CustomInfo)
{
    public bool InstanceIsolation { get; init; } = true;
    public string ModpackProject { get; init; } = "";
    public string ModpackVersion { get; init; } = "";
}
public sealed record InstanceServerPolicyFields(int LoginRequirement, string AuthServerAddress, string AuthRegisterAddress,
    string AuthServerDisplayName, string DefaultServer, bool OfflineLaunchAllowed,
    string ExpectedGameVersion, string ExpectedLoader, IReadOnlyList<string> RequiredMods);
public sealed record InstanceIdentityQuery(string InstanceDirectory);
public sealed record InstanceIdentitySnapshot(string Revision, string Identity, InstanceIdentityFields Fields,
    InstanceServerPolicyFields Server, bool AuthenticationLocked)
{
    public PngImage? Icon { get; init; }
    public string IconNotice { get; init; } = "";
    public IReadOnlyList<string> EnvironmentMismatches { get; init; } = [];
    public bool EnvironmentRequirementsDeclared { get; init; }
    public string Loader { get; init; } = "";
    public string JavaPreference { get; init; } = "";
    public int LaunchCount { get; init; }
}
public sealed record InstanceIdentitySaveCommand(string InstanceDirectory, string ExpectedRevision,
    InstanceIdentityFields Fields, InstanceServerPolicyFields Server);

public static class InstanceIdentityContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.instance.identity.query");
    public static readonly XsrSemanticId Save = XsrSemanticId.Parse("minecraft.instance.identity.save");
}
