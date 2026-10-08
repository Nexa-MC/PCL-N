using Nexa.Services.Minecraft.Launch;
using Nexa.Xsr;

namespace Nexa.Services.Settings;

public static class SettingsLaunchProfileContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("settings.launch-profile.query");
    public static readonly XsrSemanticId Save = XsrSemanticId.Parse("settings.launch-profile.save");
    public static readonly XsrSemanticId Delete = XsrSemanticId.Parse("settings.launch-profile.delete");
    public static readonly XsrSemanticId Select = XsrSemanticId.Parse("settings.launch-profile.select");
    public static readonly XsrSemanticId BeginTemporary = XsrSemanticId.Parse("settings.launch-profile.temporary.begin");
    public static readonly XsrSemanticId EndTemporary = XsrSemanticId.Parse("settings.launch-profile.temporary.end");
}

public sealed record SettingsLaunchProfile(string Id, string Name, IReadOnlyDictionary<string, SettingsOverride> Values,
    MinecraftLaunchOverlay Overlay);
public sealed record SettingsLaunchProfilesQuery(string InstanceId);
public sealed record SettingsLaunchProfilesSnapshot(long Revision, IReadOnlyList<SettingsLaunchProfile> Profiles,
    string? SelectedProfileId, string? TemporaryId)
{
    public IReadOnlyDictionary<string, SettingsOverride> TemporaryValues { get; init; } = new Dictionary<string, SettingsOverride>();
    public MinecraftLaunchOverlay TemporaryOverlay { get; init; } = new();
}
public sealed record SettingsLaunchProfileSaveCommand(string InstanceId, string ProfileId, string Name,
    IReadOnlyDictionary<string, SettingsOverride> Values, MinecraftLaunchOverlay Overlay, long ExpectedRevision);
public sealed record SettingsLaunchProfileDeleteCommand(string InstanceId, string ProfileId, long ExpectedRevision);
public sealed record SettingsLaunchProfileSelectCommand(string InstanceId, string? ProfileId, long ExpectedRevision);
public sealed record SettingsTemporaryLaunchBeginCommand(string InstanceId, string TemporaryId,
    IReadOnlyDictionary<string, SettingsOverride> Values, MinecraftLaunchOverlay Overlay, long ExpectedRevision);
public sealed record SettingsTemporaryLaunchEndCommand(string InstanceId, long ExpectedRevision);
